-- Tenants match the Keycloak organization alias with an ordinal comparison (spec section 7). Aliases are stored in
-- lower case only, so a tenant row can never differ from its organization's alias by case alone.
alter table tenancy.tenants add constraint ck_tenants_alias_lowercase check (keycloak_org_alias = lower(keycloak_org_alias));
