# Changelog

## 0.1.0 (2026-07-05)


### Features

* add INameStore + JsonNameStore for name overrides ([4ee7353](https://github.com/Dirnei/matterhorn/commit/4ee7353eee2d4cb512aa796a82922ca5ae0c0527))
* add REST DELETE /api/devices/{name} to unpair a device ([e06e884](https://github.com/Dirnei/matterhorn/commit/e06e88438c1ef5f2f61c968f88179a97ae2c4054))
* add REST POST /api/devices/{name}/rename ([5172ca3](https://github.com/Dirnei/matterhorn/commit/5172ca382aa0072c1f23e21910ac7f877b594068))
* color wheel picker + transport pill on the dashboard ([d479142](https://github.com/Dirnei/matterhorn/commit/d479142d8a97f565293cb57dd5b549fb40a7019d))
* device card action menu with inline rename and unpair ([ec00efd](https://github.com/Dirnei/matterhorn/commit/ec00efd57edc7c5b88abc3a050fc3941441eb978))
* expose transport + hue/saturation in the REST contract ([ebbb67c](https://github.com/Dirnei/matterhorn/commit/ebbb67c638dc8a42b7790b4fdf40f60c74507072))
* gate color exposes on FeatureMap, carry transport into descriptor ([d7aa545](https://github.com/Dirnei/matterhorn/commit/d7aa545c12f42b9d3a9d3c487d1ac4669976c071))
* gateway replies RemoveAccepted to a remove request ([1b6a472](https://github.com/Dirnei/matterhorn/commit/1b6a472256ff4ee2ec1c4ce039c59581ac7fcd19))
* map ColorControl hue/saturation/color_mode reads ([bc60e31](https://github.com/Dirnei/matterhorn/commit/bc60e318af6d4021bfee9113583695fb878e3e39))
* map hue/saturation sets to ColorControl commands ([12e48c6](https://github.com/Dirnei/matterhorn/commit/12e48c6ded0337e2931b9cec5408e632409355ff))
* migrate endpoint retained topics on rename ([c2da799](https://github.com/Dirnei/matterhorn/commit/c2da7990921d32f903466988b7e91a7a3755f250))
* parse transport + color features from node attributes ([14446c0](https://github.com/Dirnei/matterhorn/commit/14446c0a99890d0bc35ce9f065e2d97d447bea02))
* persist device names to a JSON file via config + docker volume ([cc10b59](https://github.com/Dirnei/matterhorn/commit/cc10b591e60b2ece46ede3288ac4f70f1e6c289f))
* rename a device from the dashboard ([1384c60](https://github.com/Dirnei/matterhorn/commit/1384c6058f285a2cc22a88d2d92d11a3fa4ff403))
* rename devices in the gateway with persisted overrides ([72e7916](https://github.com/Dirnei/matterhorn/commit/72e791618f0bf5adba941355ff0aa00f741e9258))
* route bridge/request/rename to the gateway ([f919a45](https://github.com/Dirnei/matterhorn/commit/f919a45bf7b245ca410b2ed7013aeb72198f1f16))


### Bug Fixes

* make /data writable by non-root app user so name overrides persist ([bd46310](https://github.com/Dirnei/matterhorn/commit/bd463103ba573cb32338471bf3ad6cf5102f914a))
* menu keyboard a11y — focus into menu, arrow nav, return focus on close ([e79ce54](https://github.com/Dirnei/matterhorn/commit/e79ce541b02ca1205010f0cc178068e53c95362c))
* resync package-lock.json to unblock docs build ([b72c8d1](https://github.com/Dirnei/matterhorn/commit/b72c8d160fa1d63abe8f8463e02a6d0558099ced))


### Continuous Integration

* add release-please + GHCR publish workflow ([7a2389e](https://github.com/Dirnei/matterhorn/commit/7a2389ebe429f3012591742291dda984db363ef8))
