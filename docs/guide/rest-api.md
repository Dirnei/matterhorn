# REST API

The API is generated from an authored OpenAPI document — `contracts/matterhorn.openapi.yaml` is the
source of truth. On build, NSwag generates the ASP.NET controller base + DTOs;
`Api/MatterhornController.cs` implements them against the internal model. To change the API: edit
the YAML, rebuild, implement any new operations. Browse it live at `/swagger`, served by the app via
Swagger UI.
