# REST API

The API is generated from an authored OpenAPI document — `contracts/matterhorn.openapi.yaml` is the
source of truth. On build, NSwag generates the ASP.NET controller base + DTOs;
`Api/MatterhornController.cs` implements them against the internal model. To change the API: edit
the YAML, rebuild, implement any new operations. Browse it live at `/swagger`, served by the app via
Swagger UI.

Key endpoints (see Swagger for the full contract):

| Method & path | Purpose |
|---|---|
| `GET /api/devices` | list devices and their capabilities |
| `GET /api/devices/{name}` | current state |
| `PATCH /api/devices/{name}` | [set state](/guide/control) |
| `POST /api/devices/{name}/rename` | [rename](/guide/managing-devices) |
| `DELETE /api/devices/{name}` | [unpair (decommission)](/guide/managing-devices) |
| `POST /api/commission` | [commission by setup code](/guide/commissioning) |
