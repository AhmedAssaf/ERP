-- The logo URL is rendered into every tenant page (F-02); only absolute https URLs without quoting or markup
-- characters are stored, so a value can never become a javascript: or data: URL or break out of an attribute.
alter table tenancy.tenants
    add constraint ck_tenants_logo_url check (logo_url is null or logo_url ~ '^https://[^\s"''()<>]+$');
