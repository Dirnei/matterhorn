---
layout: home

hero:
  name: Matterhorn
  text: Your Matter devices on MQTT
  tagline: Because the only thing that matters is to keep your data on your own infrastructure.
  image:
    src: /logo.png
    alt: Matterhorn
  actions:
    - theme: brand
      text: Get Started
      link: /guide/getting-started
    - theme: alt
      text: View on GitHub
      link: https://github.com/dirnei/matterhorn

features:
  - title: Commissioning & multi-admin
    details: Commission Matter-over-IP devices (Wi-Fi or Thread), including sharing a device already paired to Apple Home or Google via Matter multi-admin.
  - title: MQTT surface
    details: Retained per-device state, a bridge/devices discovery topic, and /set control — the Zigbee2MQTT topic shape.
  - title: REST API
    details: An OpenAPI-described API (GET/PATCH /api/devices/...) with Swagger UI. MQTT and REST are two projections of one model.
  - title: Web dashboard
    details: Live device cards over Server-Sent Events, with controls generated from each device's capabilities — on/off, brightness, colour temperature, an RGB colour wheel, and sensor readouts. Rename or unpair a device from its card menu.
---

<figure class="mh-figure">
  <img class="mh-shot mh-shot-light" src="/dashboard-light.png" alt="Matterhorn web dashboard showing a colour light and a motion sensor as live device cards" />
  <img class="mh-shot mh-shot-dark" src="/dashboard-dark.png" alt="Matterhorn web dashboard showing a colour light and a motion sensor as live device cards" />
  <figcaption>The bridge station — live device cards, generated from each device's capabilities.</figcaption>
</figure>
