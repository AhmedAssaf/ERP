# Keycloak realm import

Files in this folder are imported by Keycloak on start (`start-dev --import-realm`). The platform realm export (`platform-realm.json`) will be added when the identity module is designed: one realm, an Organization per tenant, a `vendor` role, and clients for the web host and the worker.

Until then Keycloak starts with only the master realm. Admin console: http://localhost:8080 with the credentials from `.env`.
