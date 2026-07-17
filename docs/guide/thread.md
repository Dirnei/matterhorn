# Thread devices

Skip this page if all your Matter devices are Wi-Fi or Ethernet — they need none of it.

## Why Thread needs a setup step

A Thread device is a low-power radio device: it has no Wi-Fi and no IP address out of the box. Two
things have to exist before one can join.

**A border router.** Thread is a separate radio network, and something has to bridge it to your LAN.
Your Matter controller host almost certainly can't — a Raspberry Pi has Wi-Fi and Bluetooth but no
802.15.4 radio — so this is a separate box.

**Credentials.** Joining a Thread network needs a network name, key, channel and more. That bundle is
called the *operational dataset* — think of it as the Wi-Fi password, but for Thread. Matterhorn
reads it off your border router and passes it to the joining device, so you never handle it yourself.

And one thing has to happen at the moment a device joins: **Bluetooth**. A factory-fresh device isn't
on any network yet, so the controller reaches it over Bluetooth just long enough to hand over the
credentials. The device then joins Thread, gets an address, and everything else happens over the
network. Bluetooth is used once, for about thirty seconds, and never again.

## Setting it up

**1. Turn on your border router.** On a SMLIGHT SLZB-MR5U: **Mode → "Thread + OTBR running on
device"**. It enables IPv6 and reboots, then serves the OpenThread REST API on port 8080.

> **Check your credentials aren't the factory demo ones.** Some border routers ship forming a network
> with OpenThread's *published* example key — `00112233445566778899aabbccddeeff`, network name
> `OpenThread-ESP`, ext PAN ID `dead00beef00cafe`. Those are printed in public documentation, so
> anyone in radio range could join. If you see them, form a new network with random credentials
> **before** joining any device — afterwards, every device has to be re-onboarded.
>
> ```bash
> curl -H 'Accept: text/plain' http://<border-router-ip>:8080/node/dataset/active
> ```

**2. Point Matterhorn at it** in your `.env`:

```bash
THREAD_OTBR_URL=http://192.168.0.233:8080
```

That's the only Thread setting. Matterhorn reads the credentials from there and re-reads them if you
ever re-form the network.

**3. Enable Bluetooth on the Matter controller.** `docker-compose.matter-server.yml` already does
this — it mounts the host's D-Bus socket and selects `hci0`. The host needs BlueZ running with the
adapter powered on (`bluetoothctl list` should show it).

## Checking it's ready

```bash
curl http://localhost:16090/api/bridge/info
```

```json
{ "service": "matterhorn",
  "thread": { "available": true, "source": "otbr", "border_router": "http://192.168.0.233:8080" } }
```

If `available` is `false`, `reason` says what's missing in plain language. Nothing here is fatal — a
border router that's unreachable only disables Thread onboarding, it never stops the bridge or your
existing devices.

## Commissioning

Exactly the same as any other device: enter the setup code (see
[Commissioning a device](/guide/commissioning)). Matterhorn works out the rest.

Two physical things matter, though, and they cause most failures:

- **Keep the device close to the controller** — within a couple of metres. Bluetooth is short-range,
  and that first handover is the only thing standing between the box and your Thread network.
- **Prefer Ethernet for the controller host.** A Raspberry Pi shares one antenna between Wi-Fi and
  Bluetooth, and the two interfere; commissioning gets flaky when the Pi is on Wi-Fi.
