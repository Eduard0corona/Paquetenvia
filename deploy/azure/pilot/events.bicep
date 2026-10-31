// ENV-001 pilot (PILOT-REAL-PEOPLE), stage 6 of 6: the Event Grid subscription that delivers the Communication Services
// e-mail delivery reports to the API (AI-05 receiveAcsEmailEvents; AI-24 recipient_notices.webhooks.email).
//
// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (O8 "Registrar al cerrar las compuertas (Recommended)") and
// NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31 (W11 items 5 and 6): Event Grid validates a subscription against its
// webhook, which answers the uniform 404 until the gate-closing change turns Messaging__Email__Events__Enabled on. So the
// subscription waits behind emailEventsPhase, whose value lives here, in the repository (never a dispatch input): with
// 'none' nothing is created; only the gate-closing change sets 'subscribe', and env001_pilot_guards.py (P28) refuses it
// without the GATE-004 and GATE-007 resolution rows. The workflow runs this stage after it has checked that the new API
// revision serves (the Event Grid handshake reaches that revision), and re-applies the current secret on every later run.
targetScope = 'resourceGroup'

@description('none: no system topic and no subscription. subscribe: the system topic of the Communication Services resource and its e-mail delivery report subscription (the gate-closing change only).')
@allowed([
  'none'
  'subscribe'
])
param emailEventsPhase string = 'none'

@description('Name of the existing Communication Services resource in the pilot resource group (PILOT_ACS_NAME).')
@minLength(1)
@maxLength(63)
param acsName string

// The workflow reads acs-email-events-secret from Key Vault in a firewall window and passes it here in a shredded
// parameters file; it is never printed. Empty, and unused, while the phase is none.
@secure()
@description('The Event Grid delivery secret (Key Vault acs-email-events-secret), sent as the static header X-Paquetenvia-Event-Secret.')
param emailEventsSecret string = ''

var tags = {
  project: 'Paquetenvia'
  environment: 'PILOT'
  contract: 'ENV-001'
  dataClassification: 'REAL_PEOPLE'
  cleanupGroup: 'ENV-001'
}

resource communicationServices 'Microsoft.Communication/communicationServices@2023-04-01' existing = {
  name: acsName
}

// Event Grid allows one system topic per source; the owner creates none by hand (guia-owner-avisos.md). A Communication
// Services resource is global, so its system topic is too.
resource emailEventsTopic 'Microsoft.EventGrid/systemTopics@2025-02-15' = if (emailEventsPhase == 'subscribe') {
  name: 'egst-pv-pilot-acs'
  location: 'global'
  tags: tags
  properties: {
    source: communicationServices.id
    topicType: 'Microsoft.Communication.CommunicationServices'
  }
}

// AI-05 receiveAcsEmailEvents: Event Grid event schema, one event per delivery (batching off), only the delivery reports
// of this resource, and the Key Vault secret in a static delivery attribute marked secret (O2 "Secreto en un encabezado
// (Recommended)"). The endpoint is the public API route, pinned by env001_pilot_guards.py; there is no IP list, because
// every delivery is authenticated by that secret (W11 item 6).
resource emailEventsSubscription 'Microsoft.EventGrid/systemTopics/eventSubscriptions@2025-02-15' = if (emailEventsPhase == 'subscribe') {
  parent: emailEventsTopic
  name: 'egsub-pv-pilot-email-events'
  properties: {
    destination: {
      endpointType: 'WebHook'
      properties: {
        // The endpoint is public (the same route Meta and every recipient's browser reach); the secret is the header.
        #disable-next-line use-secure-value-for-secure-inputs
        endpointUrl: 'https://paquetenvia.com/api/v1/messaging/email/events'
        maxEventsPerBatch: 1
        preferredBatchSizeInKilobytes: 64
        deliveryAttributeMappings: [
          {
            name: 'X-Paquetenvia-Event-Secret'
            type: 'Static'
            properties: {
              value: emailEventsSecret
              isSecret: true
            }
          }
        ]
      }
    }
    filter: {
      includedEventTypes: [
        'Microsoft.Communication.EmailDeliveryReportReceived'
      ]
    }
    eventDeliverySchema: 'EventGridSchema'
  }
}
