# How it works

<likec4-view view-id="index"></likec4-view>

Matterhorn does **not** speak Matter to devices directly. It drives a
[matterjs-server](https://github.com/matter-js/matterjs-server) instance, which does the
commissioning and low-level Matter work.

That controller must run on a **Linux host on your LAN with host networking** — Matter commissioning
needs mDNS + IPv6 link-local access to the device, which Docker Desktop on Windows/macOS can't
provide. A **Raspberry Pi** (64-bit OS, IPv6 enabled) is ideal. Matterhorn itself can run anywhere
that can reach that host.
