-- AI-06_SCHEMA.sql v0.6
-- PostgreSQL 18 + PostGIS baseline. Money uses signed bigint cents; timestamps are timestamptz UTC.
-- Apply with a privileged deployment owner. AI-18 creates roles, transfers application-object ownership to paqueteria_migrator, then applies grants.

CREATE SCHEMA IF NOT EXISTS extensions;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

-- Deliberate extension placement:
-- PostGIS remains in public for Npgsql/EF spatial operators and upgrade compatibility.
-- pgcrypto is isolated; cryptographic functions must be schema-qualified.
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA extensions;

CREATE SCHEMA IF NOT EXISTS identity;
CREATE SCHEMA IF NOT EXISTS organizations;
CREATE SCHEMA IF NOT EXISTS clients;
CREATE SCHEMA IF NOT EXISTS locations;
CREATE SCHEMA IF NOT EXISTS pricing;
CREATE SCHEMA IF NOT EXISTS orders;
CREATE SCHEMA IF NOT EXISTS dispatch;
CREATE SCHEMA IF NOT EXISTS drivers;
CREATE SCHEMA IF NOT EXISTS routes;
CREATE SCHEMA IF NOT EXISTS custody;
CREATE SCHEMA IF NOT EXISTS incidents;
CREATE SCHEMA IF NOT EXISTS finance;
CREATE SCHEMA IF NOT EXISTS allies;
CREATE SCHEMA IF NOT EXISTS notifications;
CREATE SCHEMA IF NOT EXISTS reporting;
CREATE SCHEMA IF NOT EXISTS platform;
CREATE SCHEMA IF NOT EXISTS security;

-- Reference and tenant identity -------------------------------------------------
CREATE TABLE organizations.organizations (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  legal_name text NOT NULL,
  display_name text NOT NULL,
  organization_type text NOT NULL CHECK (organization_type IN ('PLATFORM','ALLY','BUSINESS')),
  status text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','SUSPENDED','CLOSED')),
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE identity.users (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  identity_subject text NOT NULL UNIQUE,
  email_ciphertext bytea,
  status text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','SUSPENDED','DISABLED')),
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE organizations.organization_memberships (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL REFERENCES identity.users(id),
  organization_id uuid NOT NULL REFERENCES organizations.organizations(id),
  role text NOT NULL CHECK (role IN ('PLATFORM_ADMIN','DISPATCHER','FINANCE','ALLY_ADMIN','ALLY_OPERATOR','BUSINESS_ADMIN','BUSINESS_OPERATOR','DRIVER','VIEWER')),
  status text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','SUSPENDED','REVOKED')),
  is_default boolean NOT NULL DEFAULT false,
  granted_at timestamptz NOT NULL DEFAULT now(),
  revoked_at timestamptz
);
CREATE UNIQUE INDEX organization_memberships_active_role_uq
  ON organizations.organization_memberships(user_id, organization_id, role)
  WHERE status='ACTIVE';
CREATE UNIQUE INDEX organization_memberships_one_default_uq
  ON organizations.organization_memberships(user_id)
  WHERE status='ACTIVE' AND is_default;
CREATE INDEX organization_memberships_user_idx ON organizations.organization_memberships(user_id,status);
CREATE INDEX organization_memberships_org_idx ON organizations.organization_memberships(organization_id,status);

-- Geographic model -------------------------------------------------------------
CREATE TABLE locations.cities (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  country_code char(2) NOT NULL DEFAULT 'MX',
  state_code text NOT NULL,
  name text NOT NULL,
  timezone text NOT NULL,
  status text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','INACTIVE')),
  UNIQUE(country_code,state_code,name)
);

CREATE TABLE locations.service_areas (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  city_id uuid NOT NULL REFERENCES locations.cities(id),
  name text NOT NULL,
  polygon geometry(MultiPolygon,4326) NOT NULL,
  status text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','INACTIVE')),
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(owner_org_id,city_id,name)
);
CREATE INDEX service_areas_polygon_gix ON locations.service_areas USING gist(polygon);

CREATE TABLE locations.operating_zones (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  service_area_id uuid NOT NULL REFERENCES locations.service_areas(id),
  name text NOT NULL,
  zone_type text NOT NULL CHECK (zone_type IN ('CORE','STANDARD','EXTENDED','EXCLUDED')),
  polygon geometry(MultiPolygon,4326) NOT NULL,
  status text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','INACTIVE')),
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(owner_org_id,service_area_id,name)
);
CREATE INDEX operating_zones_polygon_gix ON locations.operating_zones USING gist(polygon);

CREATE TABLE locations.locations (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  city_id uuid NOT NULL REFERENCES locations.cities(id),
  service_area_id uuid REFERENCES locations.service_areas(id),
  operating_zone_id uuid REFERENCES locations.operating_zones(id),
  point geometry(Point,4326) NOT NULL,
  address_ciphertext bytea NOT NULL,
  address_summary text NOT NULL,
  contact_name_ciphertext bytea,
  phone_ciphertext bytea,
  pii_key_version text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX locations_point_gix ON locations.locations USING gist(point);
CREATE INDEX locations_owner_city_idx ON locations.locations(owner_org_id,city_id);

-- Clients and allies ------------------------------------------------------------
CREATE TABLE clients.client_accounts (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  name text NOT NULL,
  status text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','SUSPENDED','CLOSED')),
  private_tariff_id uuid,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE allies.ally_relationships (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  platform_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  ally_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  mode text NOT NULL CHECK (mode IN ('DIGITAL','OPERATIONAL_BACKUP','SHARED_CAPACITY')),
  no_solicitation_enabled boolean NOT NULL DEFAULT true,
  status text NOT NULL DEFAULT 'PILOT' CHECK (status IN ('PILOT','ACTIVE','SUSPENDED','TERMINATED')),
  effective_from timestamptz NOT NULL,
  effective_to timestamptz,
  UNIQUE(platform_org_id,ally_org_id,mode,effective_from)
);

-- Pricing ----------------------------------------------------------------------
CREATE TABLE pricing.tariff_rules (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  city_id uuid NOT NULL REFERENCES locations.cities(id),
  service_area_id uuid REFERENCES locations.service_areas(id),
  operating_zone_id uuid REFERENCES locations.operating_zones(id),
  pricing_tier text NOT NULL CHECK (pricing_tier IN ('OCCASIONAL','BUSINESS_1_49','BUSINESS_50_199','BUSINESS_200_499','BUSINESS_500_PLUS','CUSTOM')),
  service_type text NOT NULL CHECK (service_type IN ('SAME_DAY','URGENT','SCHEDULED_ROUTE')),
  amount_cents bigint NOT NULL CHECK (amount_cents >= 0),
  tax_mode text NOT NULL CHECK (tax_mode IN ('PLUS_VAT','VAT_INCLUDED','EXEMPT')),
  active_from timestamptz NOT NULL,
  active_to timestamptz,
  status text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','INACTIVE'))
);
ALTER TABLE clients.client_accounts
  ADD CONSTRAINT client_accounts_private_tariff_fk
  FOREIGN KEY (private_tariff_id) REFERENCES pricing.tariff_rules(id);

CREATE TABLE pricing.quotes (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  client_account_id uuid REFERENCES clients.client_accounts(id),
  city_id uuid NOT NULL REFERENCES locations.cities(id),
  service_area_id uuid REFERENCES locations.service_areas(id),
  origin_location_id uuid NOT NULL REFERENCES locations.locations(id),
  destination_location_id uuid NOT NULL REFERENCES locations.locations(id),
  service_type text NOT NULL CHECK (service_type IN ('SAME_DAY','URGENT','SCHEDULED_ROUTE')),
  pricing_tier text NOT NULL CHECK (pricing_tier IN ('OCCASIONAL','BUSINESS_1_49','BUSINESS_50_199','BUSINESS_200_499','BUSINESS_500_PLUS','CUSTOM')),
  consolidated_route boolean NOT NULL DEFAULT false,
  subtotal_cents bigint NOT NULL CHECK (subtotal_cents >= 0),
  discount_cents bigint NOT NULL DEFAULT 0 CHECK (discount_cents >= 0),
  tax_cents bigint NOT NULL DEFAULT 0 CHECK (tax_cents >= 0),
  total_cents bigint NOT NULL CHECK (total_cents >= 0),
  minimum_total_cents_snapshot bigint NOT NULL CHECK (minimum_total_cents_snapshot >= 0),
  currency char(3) NOT NULL DEFAULT 'MXN' CHECK (currency='MXN'),
  pricing_policy_version text NOT NULL,
  rule_ids uuid[] NOT NULL DEFAULT '{}',
  request_snapshot_redacted jsonb NOT NULL,
  package_snapshot jsonb NOT NULL,
  pii_snapshot_ciphertext bytea,
  pii_key_version text,
  breakdown jsonb NOT NULL,
  input_hash bytea NOT NULL,
  financial_override jsonb,
  status text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','USED','EXPIRED','REVOKED')),
  expires_at timestamptz NOT NULL,
  consumed_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  CHECK (total_cents = subtotal_cents - discount_cents + tax_cents),
  CHECK (pricing_tier NOT IN ('BUSINESS_200_499','BUSINESS_500_PLUS') OR consolidated_route OR COALESCE(financial_override ?& ARRAY['actor_id','reason','valid_until'],false)),
  CHECK (total_cents >= minimum_total_cents_snapshot OR COALESCE(financial_override ?& ARRAY['actor_id','reason','valid_until'],false)),
  CHECK (financial_override IS NULL OR financial_override ?& ARRAY['actor_id','reason','valid_until'])
);
CREATE INDEX quotes_owner_expiry_idx ON pricing.quotes(owner_org_id,status,expires_at);

-- Orders -----------------------------------------------------------------------
CREATE TABLE orders.orders (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  public_id text NOT NULL UNIQUE,
  quote_id uuid NOT NULL UNIQUE REFERENCES pricing.quotes(id),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  operator_org_id uuid REFERENCES organizations.organizations(id),
  client_account_id uuid REFERENCES clients.client_accounts(id),
  city_id uuid NOT NULL REFERENCES locations.cities(id),
  service_area_id uuid REFERENCES locations.service_areas(id),
  origin_location_id uuid NOT NULL REFERENCES locations.locations(id),
  destination_location_id uuid NOT NULL REFERENCES locations.locations(id),
  service_type text NOT NULL CHECK (service_type IN ('SAME_DAY','URGENT','SCHEDULED_ROUTE')),
  pricing_tier text NOT NULL CHECK (pricing_tier IN ('OCCASIONAL','BUSINESS_1_49','BUSINESS_50_199','BUSINESS_200_499','BUSINESS_500_PLUS','CUSTOM')),
  consolidated_route boolean NOT NULL DEFAULT false,
  payer_type text NOT NULL CHECK (payer_type IN ('SENDER','RECIPIENT','BUSINESS_ACCOUNT')),
  status text NOT NULL CHECK (status IN ('DRAFT','CONFIRMED','READY_FOR_PICKUP','ASSIGNED','AT_PICKUP','PICKED_UP','IN_TRANSIT','DELIVERING','FAILED_ATTEMPT','RESCHEDULED','RETURNING','RETURNED','DELIVERED','CLOSED','CLAIM_OPEN','CLAIM_RESOLVED','CANCELLED')),
  subtotal_cents bigint NOT NULL CHECK (subtotal_cents >= 0),
  discount_cents bigint NOT NULL DEFAULT 0 CHECK (discount_cents >= 0),
  tax_cents bigint NOT NULL DEFAULT 0 CHECK (tax_cents >= 0),
  total_cents bigint NOT NULL CHECK (total_cents >= 0),
  minimum_total_cents_snapshot bigint NOT NULL CHECK (minimum_total_cents_snapshot >= 0),
  currency char(3) NOT NULL DEFAULT 'MXN' CHECK (currency='MXN'),
  pricing_policy_version text NOT NULL,
  package_snapshot jsonb NOT NULL,
  financial_override jsonb,
  cod_expected_cents bigint NOT NULL DEFAULT 0 CHECK (cod_expected_cents >= 0),
  version integer NOT NULL DEFAULT 1 CHECK (version >= 1),
  claim_window_ends_at timestamptz,
  finalized_at timestamptz,
  archived_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  CHECK (total_cents = subtotal_cents - discount_cents + tax_cents),
  CHECK (pricing_tier NOT IN ('BUSINESS_200_499','BUSINESS_500_PLUS') OR consolidated_route OR COALESCE(financial_override ?& ARRAY['actor_id','reason','valid_until'],false)),
  CHECK (total_cents >= minimum_total_cents_snapshot OR COALESCE(financial_override ?& ARRAY['actor_id','reason','valid_until'],false)),
  CHECK (financial_override IS NULL OR financial_override ?& ARRAY['actor_id','reason','valid_until'])
);
CREATE INDEX orders_owner_status_idx ON orders.orders(owner_org_id,status,created_at DESC);
CREATE INDEX orders_operator_status_idx ON orders.orders(operator_org_id,status,created_at DESC);
CREATE INDEX orders_city_status_idx ON orders.orders(city_id,status,created_at DESC);
CREATE INDEX orders_claim_window_idx ON orders.orders(claim_window_ends_at) WHERE status='CLOSED' AND finalized_at IS NULL;

CREATE TABLE orders.package_items (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  order_id uuid NOT NULL REFERENCES orders.orders(id),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  operator_org_id uuid REFERENCES organizations.organizations(id),
  description text NOT NULL,
  weight_grams integer NOT NULL CHECK (weight_grams > 0),
  declared_value_cents bigint NOT NULL DEFAULT 0 CHECK (declared_value_cents >= 0),
  dimensions_mm jsonb NOT NULL DEFAULT '{}'
);
CREATE INDEX package_items_order_idx ON orders.package_items(order_id);

CREATE TABLE orders.order_events (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  order_id uuid NOT NULL REFERENCES orders.orders(id),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  operator_org_id uuid REFERENCES organizations.organizations(id),
  aggregate_version integer NOT NULL CHECK (aggregate_version >= 1),
  event_type text NOT NULL,
  public_event_code text CHECK (public_event_code IS NULL OR public_event_code IN (
    'ORDER_CREATED','PICKUP_SCHEDULED','PICKED_UP','IN_TRANSIT','OUT_FOR_DELIVERY',
    'DELIVERY_ATTEMPTED','RESCHEDULED','DELIVERED','RETURNING','RETURNED','CANCELLED'
  )),
  payload jsonb NOT NULL,
  actor_id uuid REFERENCES identity.users(id),
  occurred_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(order_id,aggregate_version)
);
CREATE INDEX order_events_tenant_order_time_idx ON orders.order_events(owner_org_id,order_id,occurred_at);
CREATE INDEX order_events_operator_time_idx ON orders.order_events(operator_org_id,occurred_at) WHERE operator_org_id IS NOT NULL;

CREATE TABLE orders.public_tracking_tokens (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  order_id uuid NOT NULL REFERENCES orders.orders(id),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  token_hash bytea NOT NULL UNIQUE CHECK (octet_length(token_hash)=32),
  expires_at timestamptz NOT NULL,
  revoked_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX tracking_tokens_order_idx ON orders.public_tracking_tokens(order_id);


CREATE TABLE orders.order_acceptances (
  id uuid PRIMARY KEY,
  order_id uuid NOT NULL UNIQUE REFERENCES orders.orders(id),
  quote_id uuid NOT NULL REFERENCES pricing.quotes(id),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  actor_id uuid REFERENCES identity.users(id),
  terms_version text NOT NULL,
  privacy_version text NOT NULL,
  accepted_at_client timestamptz NOT NULL,
  recorded_at_server timestamptz NOT NULL,
  acceptance_channel text NOT NULL CHECK (acceptance_channel IN ('WEB','PWA','ASSISTED','API')),
  evidence_schema_version text NOT NULL CHECK (evidence_schema_version='order-acceptance-v1'),
  evidence_hash bytea NOT NULL CHECK (octet_length(evidence_hash)=32)
);
CREATE INDEX order_acceptances_tenant_time_idx
  ON orders.order_acceptances(owner_org_id,recorded_at_server DESC);

-- Drivers, routes and dispatch --------------------------------------------------
CREATE TABLE drivers.driver_profiles (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL UNIQUE REFERENCES identity.users(id),
  org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  home_city_id uuid NOT NULL REFERENCES locations.cities(id),
  driver_type text NOT NULL CHECK (driver_type IN ('OWN','EXTERNAL','ALLY')),
  vehicle_type text NOT NULL CHECK (vehicle_type IN ('MOTORCYCLE','CAR','VAN','BICYCLE','WALKER')),
  status text NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING','ACTIVE','SUSPENDED','INACTIVE')),
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE drivers.driver_service_areas (
  driver_id uuid NOT NULL REFERENCES drivers.driver_profiles(id),
  service_area_id uuid NOT NULL REFERENCES locations.service_areas(id),
  org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  status text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','INACTIVE')),
  PRIMARY KEY(driver_id,service_area_id)
);

CREATE TABLE drivers.driver_documents (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  driver_id uuid NOT NULL REFERENCES drivers.driver_profiles(id),
  org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  document_type text NOT NULL CHECK (document_type IN ('IDENTITY','DRIVER_LICENSE','VEHICLE_CARD','INSURANCE','BACKGROUND_CHECK','OTHER')),
  object_key text NOT NULL,
  sha256 bytea NOT NULL,
  expires_at timestamptz,
  status text NOT NULL CHECK (status IN ('PENDING','VALID','REJECTED','EXPIRED','REVOKED')),
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX driver_documents_eligibility_idx ON drivers.driver_documents(driver_id,status,expires_at);

CREATE TABLE drivers.driver_positions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  driver_id uuid NOT NULL REFERENCES drivers.driver_profiles(id),
  org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  city_id uuid NOT NULL REFERENCES locations.cities(id),
  client_event_id uuid NOT NULL,
  point geometry(Point,4326) NOT NULL,
  accuracy_m numeric(8,2) NOT NULL CHECK (accuracy_m >= 0),
  heading_degrees numeric(6,2),
  speed_mps numeric(8,2),
  captured_at timestamptz NOT NULL,
  received_at timestamptz NOT NULL DEFAULT now(),
  publish_realtime boolean NOT NULL DEFAULT false,
  UNIQUE(driver_id,client_event_id)
);
CREATE INDEX driver_positions_tenant_time_idx ON drivers.driver_positions(org_id,driver_id,captured_at DESC);
CREATE INDEX driver_positions_captured_brin ON drivers.driver_positions USING brin(captured_at);
CREATE INDEX driver_positions_point_gix ON drivers.driver_positions USING gist(point);

CREATE TABLE routes.routes (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  operator_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  city_id uuid NOT NULL REFERENCES locations.cities(id),
  service_area_id uuid REFERENCES locations.service_areas(id),
  driver_id uuid REFERENCES drivers.driver_profiles(id),
  status text NOT NULL CHECK (status IN ('DRAFT','PLANNED','ACTIVE','COMPLETED','CANCELLED')),
  version integer NOT NULL DEFAULT 1 CHECK (version >= 1),
  scheduled_for date,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE routes.route_stops (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  route_id uuid NOT NULL REFERENCES routes.routes(id),
  order_id uuid NOT NULL REFERENCES orders.orders(id),
  operator_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  sequence integer NOT NULL CHECK (sequence > 0),
  stop_type text NOT NULL CHECK (stop_type IN ('PICKUP','DELIVERY','RETURN')),
  status text NOT NULL CHECK (status IN ('PENDING','ARRIVED','COMPLETED','FAILED','SKIPPED')),
  UNIQUE(route_id,sequence)
);

CREATE TABLE dispatch.external_offers (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  order_id uuid NOT NULL REFERENCES orders.orders(id),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  operator_org_id uuid REFERENCES organizations.organizations(id),
  commission_cents bigint NOT NULL CHECK (commission_cents >= 0),
  eligible_constraints jsonb NOT NULL DEFAULT '{"vehicle_types":[]}',
  status text NOT NULL CHECK (status IN ('OPEN','ACCEPTED','EXPIRED','CANCELLED')),
  accepted_by_driver_id uuid REFERENCES drivers.driver_profiles(id),
  accepted_at timestamptz,
  version integer NOT NULL DEFAULT 1 CHECK (version >= 1),
  expires_at timestamptz NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  CHECK ((status='ACCEPTED') = (accepted_by_driver_id IS NOT NULL AND accepted_at IS NOT NULL))
);
CREATE UNIQUE INDEX one_open_offer_per_order ON dispatch.external_offers(order_id) WHERE status='OPEN';

CREATE TABLE dispatch.assignments (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  order_id uuid NOT NULL REFERENCES orders.orders(id),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  operator_org_id uuid REFERENCES organizations.organizations(id),
  driver_id uuid NOT NULL REFERENCES drivers.driver_profiles(id),
  route_id uuid REFERENCES routes.routes(id),
  assignment_type text NOT NULL CHECK (assignment_type IN ('OWN','EXTERNAL','ALLY_CAPACITY')),
  status text NOT NULL CHECK (status IN ('OFFERED','ACCEPTED','ACTIVE','COMPLETED','CANCELLED')),
  cost_cents bigint NOT NULL CHECK (cost_cents >= 0),
  accepted_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX one_active_assignment_per_order ON dispatch.assignments(order_id) WHERE status IN ('ACCEPTED','ACTIVE');

-- Custody and incidents ---------------------------------------------------------
CREATE TABLE custody.proof_upload_sessions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  order_id uuid NOT NULL REFERENCES orders.orders(id),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  operator_org_id uuid REFERENCES organizations.organizations(id),
  requested_by uuid NOT NULL REFERENCES identity.users(id),
  object_key_quarantine text NOT NULL UNIQUE,
  expected_content_type text NOT NULL,
  maximum_bytes bigint NOT NULL CHECK (maximum_bytes > 0),
  status text NOT NULL CHECK (status IN ('CREATED','UPLOADED','VALIDATING','READY','REJECTED','EXPIRED','CONSUMED')),
  expires_at timestamptz NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX proof_upload_sessions_expiry_idx ON custody.proof_upload_sessions(status,expires_at);

CREATE TABLE custody.proofs (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  order_id uuid NOT NULL REFERENCES orders.orders(id),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  operator_org_id uuid REFERENCES organizations.organizations(id),
  upload_session_id uuid NOT NULL UNIQUE REFERENCES custody.proof_upload_sessions(id),
  proof_type text NOT NULL CHECK (proof_type IN ('PICKUP_PHOTO','DELIVERY_PHOTO','SIGNATURE','DELIVERY_CODE','RETURN_PHOTO')),
  object_key text NOT NULL UNIQUE,
  sha256 bytea NOT NULL,
  content_type text NOT NULL,
  size_bytes bigint NOT NULL CHECK (size_bytes > 0),
  recipient_name_ciphertext bytea,
  pii_key_version text,
  captured_at timestamptz NOT NULL,
  captured_point geometry(Point,4326),
  created_by uuid NOT NULL REFERENCES identity.users(id),
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX proofs_tenant_order_idx ON custody.proofs(owner_org_id,order_id,created_at);

CREATE TABLE incidents.incidents (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  order_id uuid NOT NULL REFERENCES orders.orders(id),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  operator_org_id uuid REFERENCES organizations.organizations(id),
  incident_type text NOT NULL,
  severity text NOT NULL CHECK (severity IN ('LOW','MEDIUM','HIGH','CRITICAL')),
  status text NOT NULL CHECK (status IN ('OPEN','INVESTIGATING','RESOLVED','REJECTED')),
  custody_acquired boolean NOT NULL DEFAULT false,
  description_ciphertext bytea,
  pii_key_version text,
  created_by uuid NOT NULL REFERENCES identity.users(id),
  created_at timestamptz NOT NULL DEFAULT now(),
  resolved_at timestamptz
);
CREATE INDEX incidents_tenant_order_idx ON incidents.incidents(owner_org_id,order_id,status);

-- Finance ----------------------------------------------------------------------
CREATE TABLE finance.cod_transactions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  order_id uuid NOT NULL REFERENCES orders.orders(id),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  operator_org_id uuid REFERENCES organizations.organizations(id),
  amount_cents bigint NOT NULL CHECK (amount_cents > 0),
  status text NOT NULL CHECK (status IN ('EXPECTED','RECORDED','RECONCILED','DISPUTED','REVERSED')),
  collected_by_driver_id uuid REFERENCES drivers.driver_profiles(id),
  recorded_at timestamptz,
  reconciled_at timestamptz,
  reference text,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(order_id)
);

CREATE TABLE finance.settlements (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  payee_type text NOT NULL CHECK (payee_type IN ('DRIVER','ALLY','BUSINESS')),
  payee_id uuid NOT NULL,
  status text NOT NULL CHECK (status IN ('DRAFT','CALCULATED','APPROVED','PAID','VOID')),
  total_cents bigint NOT NULL DEFAULT 0,
  period_from date NOT NULL,
  period_to date NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  CHECK (period_to >= period_from)
);

CREATE TABLE finance.settlement_lines (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  settlement_id uuid NOT NULL REFERENCES finance.settlements(id),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  order_id uuid REFERENCES orders.orders(id),
  line_type text NOT NULL CHECK (line_type IN ('DELIVERY','ROUTE_BASE','BONUS','WAITING','RETURN','COD','ADJUSTMENT')),
  amount_cents bigint NOT NULL,
  source_reference text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(settlement_id,source_reference)
);
CREATE INDEX settlement_lines_settlement_idx ON finance.settlement_lines(settlement_id);

-- Notifications and reporting --------------------------------------------------
CREATE TABLE notifications.notifications (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  order_id uuid REFERENCES orders.orders(id),
  channel text NOT NULL CHECK (channel IN ('EMAIL','SMS','WHATSAPP','PUSH','IN_APP')),
  template_key text NOT NULL,
  recipient_ciphertext bytea,
  pii_key_version text,
  status text NOT NULL CHECK (status IN ('PENDING','SENT','DELIVERED','FAILED','CANCELLED')),
  attempts integer NOT NULL DEFAULT 0,
  provider_reference text,
  created_at timestamptz NOT NULL DEFAULT now(),
  sent_at timestamptz
);

CREATE TABLE reporting.report_exports (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  requested_by uuid NOT NULL REFERENCES identity.users(id),
  report_type text NOT NULL,
  parameters jsonb NOT NULL,
  status text NOT NULL CHECK (status IN ('QUEUED','RUNNING','READY','FAILED','EXPIRED')),
  object_key text,
  expires_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now()
);

-- Platform services -------------------------------------------------------------
CREATE TABLE platform.audit_logs (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  actor_id uuid REFERENCES identity.users(id),
  action text NOT NULL,
  entity_type text NOT NULL,
  entity_id uuid NOT NULL,
  request_id text,
  payload_redacted jsonb NOT NULL DEFAULT '{}',
  occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX audit_logs_tenant_time_idx ON platform.audit_logs(org_id,occurred_at DESC);

CREATE TABLE platform.outbox_events (
  id uuid PRIMARY KEY,
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  tenant_context jsonb NOT NULL,
  topic text NOT NULL,
  aggregate_type text NOT NULL,
  aggregate_id uuid NOT NULL,
  aggregate_version integer,
  payload jsonb NOT NULL,
  priority smallint NOT NULL CHECK (priority BETWEEN 0 AND 100),
  status text NOT NULL CHECK (status IN ('PENDING','PROCESSING','RETRY','PROCESSED','DEAD')),
  attempts integer NOT NULL CHECK (attempts >= 0),
  available_at timestamptz NOT NULL,
  locked_at timestamptz,
  locked_by text,
  lease_token uuid,
  lease_expires_at timestamptz,
  last_error text,
  created_at timestamptz NOT NULL,
  processed_at timestamptz,
  CHECK (
    (status='PROCESSING' AND lease_token IS NOT NULL AND lease_expires_at IS NOT NULL AND locked_at IS NOT NULL AND locked_by IS NOT NULL)
    OR
    (status<>'PROCESSING' AND lease_token IS NULL AND lease_expires_at IS NULL)
  )
);
CREATE INDEX outbox_claim_idx ON platform.outbox_events(priority DESC,available_at,created_at) WHERE status IN ('PENDING','RETRY');
CREATE INDEX outbox_processing_lease_idx ON platform.outbox_events(lease_expires_at) WHERE status='PROCESSING';
CREATE INDEX outbox_tenant_aggregate_idx ON platform.outbox_events(owner_org_id,aggregate_type,aggregate_id,created_at);

CREATE TABLE platform.location_outbox_events (
  id uuid PRIMARY KEY,
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  driver_position_id uuid NOT NULL UNIQUE,
  topic text NOT NULL,
  payload jsonb NOT NULL,
  status text NOT NULL CHECK (status IN ('PENDING','PROCESSING','RETRY','PROCESSED','DEAD')),
  attempts integer NOT NULL CHECK (attempts >= 0),
  available_at timestamptz NOT NULL,
  locked_at timestamptz,
  locked_by text,
  lease_token uuid,
  lease_expires_at timestamptz,
  last_error text,
  created_at timestamptz NOT NULL,
  processed_at timestamptz,
  CHECK (
    (status='PROCESSING' AND lease_token IS NOT NULL AND lease_expires_at IS NOT NULL AND locked_at IS NOT NULL AND locked_by IS NOT NULL)
    OR
    (status<>'PROCESSING' AND lease_token IS NULL AND lease_expires_at IS NULL)
  )
);
CREATE INDEX location_outbox_claim_idx ON platform.location_outbox_events(available_at,created_at) WHERE status IN ('PENDING','RETRY');
CREATE INDEX location_outbox_processing_lease_idx ON platform.location_outbox_events(lease_expires_at) WHERE status='PROCESSING';
CREATE INDEX location_outbox_position_idx ON platform.location_outbox_events(driver_position_id,created_at);

CREATE TABLE platform.idempotency_keys (
  owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
  scope text NOT NULL,
  idempotency_key text NOT NULL,
  request_hash bytea NOT NULL,
  response_status integer,
  response_body jsonb,
  resource_id uuid,
  created_at timestamptz NOT NULL DEFAULT now(),
  expires_at timestamptz NOT NULL,
  PRIMARY KEY(owner_org_id,scope,idempotency_key)
);
CREATE INDEX idempotency_expiry_idx ON platform.idempotency_keys(expires_at);

-- Tenant context and RLS --------------------------------------------------------
CREATE OR REPLACE FUNCTION security.app_current_user() RETURNS uuid
LANGUAGE sql STABLE PARALLEL SAFE AS $$
  SELECT NULLIF(current_setting('app.current_user_id', true),'')::uuid;
$$;

CREATE OR REPLACE FUNCTION security.app_allowed_org(p_org uuid) RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
  SELECT p_org IS NOT NULL
     AND p_org = ANY(COALESCE(NULLIF(current_setting('app.current_org_ids', true),'')::uuid[], ARRAY[]::uuid[]));
$$;

-- Bootstrap functions. Ownership changes to paqueteria_bootstrap in AI-18.
CREATE OR REPLACE FUNCTION security.resolve_identity_context(p_identity_subject text)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, identity, organizations, security, pg_temp
AS $$
DECLARE v_result jsonb;
BEGIN
  SELECT jsonb_build_object(
    'user_id', u.id,
    'status', u.status,
    'memberships', COALESCE(jsonb_agg(jsonb_build_object(
      'organization_id', m.organization_id,
      'role', m.role,
      'is_default', m.is_default
    ) ORDER BY m.is_default DESC, m.organization_id) FILTER (WHERE m.id IS NOT NULL), '[]'::jsonb)
  ) INTO v_result
  FROM identity.users u
  LEFT JOIN organizations.organization_memberships m
    ON m.user_id=u.id AND m.status='ACTIVE'
  WHERE u.identity_subject=p_identity_subject AND u.status='ACTIVE'
  GROUP BY u.id,u.status;
  RETURN v_result;
END $$;

CREATE OR REPLACE FUNCTION security.map_public_order_status(p_status text)
RETURNS text
LANGUAGE sql
IMMUTABLE
PARALLEL SAFE
SET search_path = pg_catalog, pg_temp
AS $$
  SELECT CASE p_status
    WHEN 'DRAFT' THEN 'CREATED'
    WHEN 'CONFIRMED' THEN 'CREATED'
    WHEN 'READY_FOR_PICKUP' THEN 'SCHEDULED'
    WHEN 'ASSIGNED' THEN 'SCHEDULED'
    WHEN 'AT_PICKUP' THEN 'SCHEDULED'
    WHEN 'PICKED_UP' THEN 'IN_TRANSIT'
    WHEN 'IN_TRANSIT' THEN 'IN_TRANSIT'
    WHEN 'DELIVERING' THEN 'OUT_FOR_DELIVERY'
    WHEN 'FAILED_ATTEMPT' THEN 'DELIVERY_EXCEPTION'
    WHEN 'RESCHEDULED' THEN 'SCHEDULED'
    WHEN 'RETURNING' THEN 'RETURNING'
    WHEN 'RETURNED' THEN 'RETURNED'
    WHEN 'DELIVERED' THEN 'DELIVERED'
    WHEN 'CLOSED' THEN 'DELIVERED'
    WHEN 'CLAIM_OPEN' THEN 'DELIVERED'
    WHEN 'CLAIM_RESOLVED' THEN 'DELIVERED'
    WHEN 'CANCELLED' THEN 'CANCELLED'
    ELSE NULL
  END;
$$;

CREATE OR REPLACE FUNCTION security.get_public_tracking_projection(p_token text)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, extensions, orders, security, pg_temp
AS $$
DECLARE v_result jsonb;
BEGIN
  SELECT jsonb_build_object(
    'public_id', o.public_id,
    'public_status', security.map_public_order_status(o.status),
    'estimated_window', NULL,
    'timeline', COALESCE((
      SELECT jsonb_agg(
        jsonb_build_object('code',e.public_event_code,'occurred_at',e.occurred_at)
        ORDER BY e.occurred_at
      )
      FROM orders.order_events e
      WHERE e.order_id=o.id
        AND e.public_event_code IS NOT NULL
    ), '[]'::jsonb)
  ) INTO v_result
  FROM orders.public_tracking_tokens t
  JOIN orders.orders o ON o.id=t.order_id
  WHERE t.token_hash=extensions.digest(pg_catalog.convert_to(p_token,'UTF8'),'sha256')
    AND t.revoked_at IS NULL
    AND t.expires_at > clock_timestamp()
    AND security.map_public_order_status(o.status) IS NOT NULL;
  RETURN v_result;
END $$;

-- Lease-protected outbox lifecycle. Ownership changes in AI-18.
CREATE OR REPLACE FUNCTION security.claim_outbox(
  p_worker_id text,
  p_batch_size integer DEFAULT 50,
  p_lease_duration interval DEFAULT interval '2 minutes'
)
RETURNS SETOF platform.outbox_events
LANGUAGE sql
SECURITY DEFINER
SET search_path = pg_catalog, platform, security, pg_temp
AS $$
  WITH candidates AS (
    SELECT id FROM platform.outbox_events
    WHERE status IN ('PENDING','RETRY') AND available_at <= clock_timestamp()
    ORDER BY priority DESC, available_at, created_at
    FOR UPDATE SKIP LOCKED
    LIMIT LEAST(GREATEST(p_batch_size,1),200)
  )
  UPDATE platform.outbox_events o
     SET status='PROCESSING',
         attempts=o.attempts+1,
         locked_at=clock_timestamp(),
         locked_by=p_worker_id,
         lease_token=pg_catalog.gen_random_uuid(),
         lease_expires_at=clock_timestamp()+GREATEST(p_lease_duration,interval '10 seconds'),
         last_error=NULL
    FROM candidates c
   WHERE o.id=c.id
  RETURNING o.*;
$$;

CREATE OR REPLACE FUNCTION security.settle_outbox(
  p_id uuid,
  p_lease_token uuid,
  p_status text,
  p_error text DEFAULT NULL,
  p_available_at timestamptz DEFAULT NULL
)
RETURNS boolean
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, platform, security, pg_temp
AS $$
BEGIN
  IF p_status NOT IN ('PROCESSED','RETRY','DEAD') THEN
    RAISE EXCEPTION 'invalid outbox settle status %',p_status USING ERRCODE='22023';
  END IF;
  UPDATE platform.outbox_events
     SET status=p_status,
         last_error=CASE WHEN p_status='PROCESSED' THEN NULL ELSE left(p_error,4000) END,
         processed_at=CASE WHEN p_status IN ('PROCESSED','DEAD') THEN clock_timestamp() ELSE NULL END,
         available_at=CASE WHEN p_status='RETRY' THEN COALESCE(p_available_at,clock_timestamp()+interval '30 seconds') ELSE available_at END,
         locked_at=NULL, locked_by=NULL, lease_token=NULL, lease_expires_at=NULL
   WHERE id=p_id
     AND status='PROCESSING'
     AND lease_token=p_lease_token
     AND lease_expires_at > clock_timestamp();
  RETURN FOUND;
END $$;

CREATE OR REPLACE FUNCTION security.requeue_stale_outbox(
  p_older_than interval DEFAULT interval '0 seconds',
  p_batch_size integer DEFAULT 100,
  p_max_attempts integer DEFAULT 10
)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, platform, security, pg_temp
AS $$
DECLARE v_count integer;
BEGIN
  WITH candidates AS (
    SELECT id FROM platform.outbox_events
    WHERE status='PROCESSING'
      AND lease_expires_at <= clock_timestamp()-GREATEST(p_older_than,interval '0 seconds')
    ORDER BY lease_expires_at
    FOR UPDATE SKIP LOCKED
    LIMIT LEAST(GREATEST(p_batch_size,1),1000)
  ), updated AS (
    UPDATE platform.outbox_events o
       SET status=CASE WHEN o.attempts >= GREATEST(p_max_attempts,1) THEN 'DEAD' ELSE 'RETRY' END,
           available_at=clock_timestamp(),
           processed_at=CASE WHEN o.attempts >= GREATEST(p_max_attempts,1) THEN clock_timestamp() ELSE NULL END,
           last_error=COALESCE(o.last_error,'lease expired before settle'),
           locked_at=NULL, locked_by=NULL, lease_token=NULL, lease_expires_at=NULL
      FROM candidates c WHERE o.id=c.id
    RETURNING 1
  )
  SELECT count(*) INTO v_count FROM updated;
  RETURN v_count;
END $$;

CREATE OR REPLACE FUNCTION security.claim_location_outbox(
  p_worker_id text,
  p_batch_size integer DEFAULT 100,
  p_lease_duration interval DEFAULT interval '1 minute'
)
RETURNS SETOF platform.location_outbox_events
LANGUAGE sql
SECURITY DEFINER
SET search_path = pg_catalog, platform, security, pg_temp
AS $$
  WITH candidates AS (
    SELECT id FROM platform.location_outbox_events
    WHERE status IN ('PENDING','RETRY') AND available_at <= clock_timestamp()
    ORDER BY available_at, created_at
    FOR UPDATE SKIP LOCKED
    LIMIT LEAST(GREATEST(p_batch_size,1),500)
  )
  UPDATE platform.location_outbox_events o
     SET status='PROCESSING',
         attempts=o.attempts+1,
         locked_at=clock_timestamp(),
         locked_by=p_worker_id,
         lease_token=pg_catalog.gen_random_uuid(),
         lease_expires_at=clock_timestamp()+GREATEST(p_lease_duration,interval '10 seconds'),
         last_error=NULL
    FROM candidates c
   WHERE o.id=c.id
  RETURNING o.*;
$$;

CREATE OR REPLACE FUNCTION security.settle_location_outbox(
  p_id uuid,
  p_lease_token uuid,
  p_status text,
  p_error text DEFAULT NULL,
  p_available_at timestamptz DEFAULT NULL
)
RETURNS boolean
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, platform, security, pg_temp
AS $$
BEGIN
  IF p_status NOT IN ('PROCESSED','RETRY','DEAD') THEN
    RAISE EXCEPTION 'invalid location outbox settle status %',p_status USING ERRCODE='22023';
  END IF;
  UPDATE platform.location_outbox_events
     SET status=p_status,
         last_error=CASE WHEN p_status='PROCESSED' THEN NULL ELSE left(p_error,4000) END,
         processed_at=CASE WHEN p_status IN ('PROCESSED','DEAD') THEN clock_timestamp() ELSE NULL END,
         available_at=CASE WHEN p_status='RETRY' THEN COALESCE(p_available_at,clock_timestamp()+interval '15 seconds') ELSE available_at END,
         locked_at=NULL, locked_by=NULL, lease_token=NULL, lease_expires_at=NULL
   WHERE id=p_id
     AND status='PROCESSING'
     AND lease_token=p_lease_token
     AND lease_expires_at > clock_timestamp();
  RETURN FOUND;
END $$;

CREATE OR REPLACE FUNCTION security.requeue_stale_location_outbox(
  p_older_than interval DEFAULT interval '0 seconds',
  p_batch_size integer DEFAULT 500,
  p_max_attempts integer DEFAULT 20
)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, platform, security, pg_temp
AS $$
DECLARE v_count integer;
BEGIN
  WITH candidates AS (
    SELECT id FROM platform.location_outbox_events
    WHERE status='PROCESSING'
      AND lease_expires_at <= clock_timestamp()-GREATEST(p_older_than,interval '0 seconds')
    ORDER BY lease_expires_at
    FOR UPDATE SKIP LOCKED
    LIMIT LEAST(GREATEST(p_batch_size,1),5000)
  ), updated AS (
    UPDATE platform.location_outbox_events o
       SET status=CASE WHEN o.attempts >= GREATEST(p_max_attempts,1) THEN 'DEAD' ELSE 'RETRY' END,
           available_at=clock_timestamp(),
           processed_at=CASE WHEN o.attempts >= GREATEST(p_max_attempts,1) THEN clock_timestamp() ELSE NULL END,
           last_error=COALESCE(o.last_error,'location lease expired before settle'),
           locked_at=NULL, locked_by=NULL, lease_token=NULL, lease_expires_at=NULL
      FROM candidates c WHERE o.id=c.id
    RETURNING 1
  )
  SELECT count(*) INTO v_count FROM updated;
  RETURN v_count;
END $$;

-- Retention functions are owned by paqueteria_maintenance and never touch active states.
CREATE OR REPLACE FUNCTION security.purge_outbox(
  p_processed_before timestamptz,
  p_dead_before timestamptz,
  p_batch_size integer DEFAULT 1000,
  p_dry_run boolean DEFAULT true
)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, platform, security, pg_temp
AS $$
DECLARE v_count integer;
BEGIN
  IF p_processed_before > clock_timestamp()-interval '1 day' OR p_dead_before > clock_timestamp()-interval '7 days' THEN
    RAISE EXCEPTION 'purge cutoff is inside minimum retention window' USING ERRCODE='22023';
  END IF;
  IF p_dry_run THEN
    SELECT count(*) INTO v_count FROM (
      SELECT id FROM platform.outbox_events
      WHERE (status='PROCESSED' AND processed_at < p_processed_before)
         OR (status='DEAD' AND COALESCE(processed_at,created_at) < p_dead_before)
      ORDER BY created_at
      LIMIT LEAST(GREATEST(p_batch_size,1),10000)
    ) q;
    RETURN v_count;
  END IF;
  WITH candidates AS (
    SELECT id FROM platform.outbox_events
    WHERE (status='PROCESSED' AND processed_at < p_processed_before)
       OR (status='DEAD' AND COALESCE(processed_at,created_at) < p_dead_before)
    ORDER BY created_at
    FOR UPDATE SKIP LOCKED
    LIMIT LEAST(GREATEST(p_batch_size,1),10000)
  ), deleted AS (
    DELETE FROM platform.outbox_events o USING candidates c WHERE o.id=c.id RETURNING 1
  )
  SELECT count(*) INTO v_count FROM deleted;
  RETURN v_count;
END $$;

CREATE OR REPLACE FUNCTION security.purge_location_outbox(
  p_processed_before timestamptz,
  p_dead_before timestamptz,
  p_batch_size integer DEFAULT 5000,
  p_dry_run boolean DEFAULT true
)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, platform, security, pg_temp
AS $$
DECLARE v_count integer;
BEGIN
  IF p_processed_before > clock_timestamp()-interval '1 hour' OR p_dead_before > clock_timestamp()-interval '1 day' THEN
    RAISE EXCEPTION 'location purge cutoff is inside minimum retention window' USING ERRCODE='22023';
  END IF;
  IF p_dry_run THEN
    SELECT count(*) INTO v_count FROM (
      SELECT id FROM platform.location_outbox_events
      WHERE (status='PROCESSED' AND processed_at < p_processed_before)
         OR (status='DEAD' AND COALESCE(processed_at,created_at) < p_dead_before)
      ORDER BY created_at
      LIMIT LEAST(GREATEST(p_batch_size,1),50000)
    ) q;
    RETURN v_count;
  END IF;
  WITH candidates AS (
    SELECT id FROM platform.location_outbox_events
    WHERE (status='PROCESSED' AND processed_at < p_processed_before)
       OR (status='DEAD' AND COALESCE(processed_at,created_at) < p_dead_before)
    ORDER BY created_at
    FOR UPDATE SKIP LOCKED
    LIMIT LEAST(GREATEST(p_batch_size,1),50000)
  ), deleted AS (
    DELETE FROM platform.location_outbox_events o USING candidates c WHERE o.id=c.id RETURNING 1
  )
  SELECT count(*) INTO v_count FROM deleted;
  RETURN v_count;
END $$;

-- RLS direct tenant table registry.
DO $$
DECLARE item text;
BEGIN
  FOREACH item IN ARRAY ARRAY[
    'organizations.organizations','identity.users','organizations.organization_memberships',
    'clients.client_accounts','allies.ally_relationships','locations.service_areas','locations.operating_zones','locations.locations',
    'pricing.tariff_rules','pricing.quotes','orders.orders','orders.package_items','orders.order_events','orders.public_tracking_tokens','orders.order_acceptances',
    'drivers.driver_profiles','drivers.driver_service_areas','drivers.driver_documents','drivers.driver_positions',
    'routes.routes','routes.route_stops','dispatch.external_offers','dispatch.assignments',
    'custody.proof_upload_sessions','custody.proofs','incidents.incidents',
    'finance.cod_transactions','finance.settlements','finance.settlement_lines',
    'notifications.notifications','reporting.report_exports','platform.audit_logs','platform.outbox_events','platform.location_outbox_events','platform.idempotency_keys'
  ] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', item);
    EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', item);
  END LOOP;
END $$;

CREATE POLICY organizations_tenant ON organizations.organizations
  USING (security.app_allowed_org(id)) WITH CHECK (security.app_allowed_org(id));
CREATE POLICY users_tenant ON identity.users
  USING (id=security.app_current_user() OR EXISTS (
    SELECT 1 FROM organizations.organization_memberships m
    WHERE m.user_id=identity.users.id AND security.app_allowed_org(m.organization_id)))
  WITH CHECK (id=security.app_current_user());
CREATE POLICY memberships_tenant ON organizations.organization_memberships
  USING (security.app_allowed_org(organization_id) OR user_id=security.app_current_user())
  WITH CHECK (security.app_allowed_org(organization_id));
CREATE POLICY client_accounts_tenant ON clients.client_accounts USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));
CREATE POLICY ally_relationships_tenant ON allies.ally_relationships USING (security.app_allowed_org(platform_org_id) OR security.app_allowed_org(ally_org_id)) WITH CHECK (security.app_allowed_org(platform_org_id) OR security.app_allowed_org(ally_org_id));
CREATE POLICY service_areas_tenant ON locations.service_areas USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));
CREATE POLICY operating_zones_tenant ON locations.operating_zones USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));
CREATE POLICY locations_tenant ON locations.locations USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));
CREATE POLICY tariff_rules_tenant ON pricing.tariff_rules USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));
CREATE POLICY quotes_tenant ON pricing.quotes USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));
CREATE POLICY orders_tenant ON orders.orders USING (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id));
CREATE POLICY package_items_tenant ON orders.package_items USING (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id));
CREATE POLICY order_events_tenant ON orders.order_events USING (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id));
CREATE POLICY tracking_tokens_tenant ON orders.public_tracking_tokens USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));
CREATE POLICY order_acceptances_tenant ON orders.order_acceptances USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));
CREATE POLICY driver_profiles_tenant ON drivers.driver_profiles USING (security.app_allowed_org(org_id)) WITH CHECK (security.app_allowed_org(org_id));
CREATE POLICY driver_service_areas_tenant ON drivers.driver_service_areas USING (security.app_allowed_org(org_id)) WITH CHECK (security.app_allowed_org(org_id));
CREATE POLICY driver_documents_tenant ON drivers.driver_documents USING (security.app_allowed_org(org_id)) WITH CHECK (security.app_allowed_org(org_id));
CREATE POLICY driver_positions_tenant ON drivers.driver_positions USING (security.app_allowed_org(org_id)) WITH CHECK (security.app_allowed_org(org_id));
CREATE POLICY routes_tenant ON routes.routes USING (security.app_allowed_org(operator_org_id)) WITH CHECK (security.app_allowed_org(operator_org_id));
CREATE POLICY route_stops_tenant ON routes.route_stops USING (security.app_allowed_org(operator_org_id)) WITH CHECK (security.app_allowed_org(operator_org_id));
CREATE POLICY external_offers_tenant ON dispatch.external_offers USING (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id));
CREATE POLICY assignments_tenant ON dispatch.assignments USING (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id));
CREATE POLICY proof_upload_sessions_tenant ON custody.proof_upload_sessions USING (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id));
CREATE POLICY proofs_tenant ON custody.proofs USING (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id));
CREATE POLICY incidents_tenant ON incidents.incidents USING (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id));
CREATE POLICY cod_tenant ON finance.cod_transactions USING (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id));
CREATE POLICY settlements_tenant ON finance.settlements USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));
CREATE POLICY settlement_lines_tenant ON finance.settlement_lines USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));
CREATE POLICY notifications_tenant ON notifications.notifications USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));
CREATE POLICY report_exports_tenant ON reporting.report_exports USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));
CREATE POLICY audit_logs_tenant ON platform.audit_logs USING (security.app_allowed_org(org_id)) WITH CHECK (security.app_allowed_org(org_id));
CREATE POLICY outbox_tenant ON platform.outbox_events USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));
CREATE POLICY location_outbox_tenant ON platform.location_outbox_events USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));
CREATE POLICY idempotency_tenant ON platform.idempotency_keys USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));

-- Immutable content defense -----------------------------------------------------
CREATE OR REPLACE FUNCTION platform.reject_runtime_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF current_user <> 'paqueteria_migrator' THEN
    RAISE EXCEPTION '% is append-only', TG_TABLE_SCHEMA || '.' || TG_TABLE_NAME USING ERRCODE='42501';
  END IF;
  RETURN CASE WHEN TG_OP='DELETE' THEN OLD ELSE NEW END;
END $$;

CREATE TRIGGER order_events_append_only BEFORE UPDATE OR DELETE ON orders.order_events
  FOR EACH ROW EXECUTE FUNCTION platform.reject_runtime_mutation();
CREATE TRIGGER order_acceptances_append_only BEFORE UPDATE OR DELETE ON orders.order_acceptances
  FOR EACH ROW EXECUTE FUNCTION platform.reject_runtime_mutation();
CREATE TRIGGER proofs_append_only BEFORE UPDATE OR DELETE ON custody.proofs
  FOR EACH ROW EXECUTE FUNCTION platform.reject_runtime_mutation();
CREATE TRIGGER audit_logs_append_only BEFORE UPDATE OR DELETE ON platform.audit_logs
  FOR EACH ROW EXECUTE FUNCTION platform.reject_runtime_mutation();

CREATE OR REPLACE FUNCTION platform.protect_outbox_content() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF (NEW.owner_org_id,NEW.tenant_context,NEW.topic,NEW.aggregate_type,NEW.aggregate_id,NEW.aggregate_version,NEW.payload,NEW.priority,NEW.created_at)
     IS DISTINCT FROM
     (OLD.owner_org_id,OLD.tenant_context,OLD.topic,OLD.aggregate_type,OLD.aggregate_id,OLD.aggregate_version,OLD.payload,OLD.priority,OLD.created_at) THEN
    RAISE EXCEPTION 'outbox event content is immutable' USING ERRCODE='42501';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER outbox_content_immutable BEFORE UPDATE ON platform.outbox_events
  FOR EACH ROW EXECUTE FUNCTION platform.protect_outbox_content();

CREATE OR REPLACE FUNCTION platform.protect_location_outbox_content() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF (NEW.owner_org_id,NEW.driver_position_id,NEW.topic,NEW.payload,NEW.created_at)
     IS DISTINCT FROM
     (OLD.owner_org_id,OLD.driver_position_id,OLD.topic,OLD.payload,OLD.created_at) THEN
    RAISE EXCEPTION 'location outbox event content is immutable' USING ERRCODE='42501';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER location_outbox_content_immutable BEFORE UPDATE ON platform.location_outbox_events
  FOR EACH ROW EXECUTE FUNCTION platform.protect_location_outbox_content();

-- Global reference cities are read-only to runtime roles and intentionally do not use RLS.
-- Retention, encryption key management and partition activation remain gated by GATE-007/GATE-014.
