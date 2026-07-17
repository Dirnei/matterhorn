# Commissioning a device

Open the dashboard and use the **Commission** field:

- **A brand-new device** — enter its setup code (the `MT:…` QR string or the 11-digit manual code).
- **A device already in Apple Home** (multi-admin) — in the Home app, open the accessory →
  **Turn On Pairing Mode**, and enter the code it shows you. The device joins Matterhorn's fabric
  *in addition to* Apple Home; both control it independently.
- **A brand-new Thread device** — same setup code, but it needs a border router and Bluetooth in
  place first, one time. See [Thread devices](/guide/thread). Wi-Fi and Ethernet devices don't.

The device appears on the dashboard once the controller finishes interviewing it (~30–60s).

To rename it or remove it from the fabric, see [Renaming & unpairing](/guide/managing-devices).
