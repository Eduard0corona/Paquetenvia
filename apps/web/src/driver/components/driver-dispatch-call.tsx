import {
  dispatchPhoneE164Pattern,
  driverDispatchCallHref,
  driverDispatchCallLabel,
} from "../contracts/dispatch-contact";
import styles from "./driver-stops.module.css";

/**
 * DRV-DISPATCH-CONTACT-2026-10-12 (project owner, "Sí, configurable
 * (Recommended)"): "Llamar a despacho", a 44 px `tel:` link to the
 * organization's dispatch number (getMyDispatchContact). It goes only next to
 * a text that asks the driver to call dispatch after a recorded cash
 * collection; without a known number nothing is rendered and the text stays
 * as it is. The number itself is never shown as text.
 */
export function DriverDispatchCall({
  phoneE164,
}: Readonly<{ phoneE164: string | null }>) {
  if (phoneE164 === null || !dispatchPhoneE164Pattern.test(phoneE164)) {
    return null;
  }
  return (
    <a className={styles.dispatchCall} href={driverDispatchCallHref(phoneE164)}>
      {driverDispatchCallLabel}
    </a>
  );
}
