-- ADR-0012 point 2: vendor.relationships is the one tenant table a vendor session reads today, and only its own row (the
-- vendor's status with the host tenant, IVendorCompanies). The staff-only rule that platform migration 0006 gave every
-- tenant table would hide that row; the explicit vendor helper keeps it visible to its own company and hides every other
-- company's relationship with the tenant from a vendor session. Staff sessions (no vendor context) keep the tenant rule.
-- The application role still only selects the table; rows change through the security-definer functions of 0003 and 0010.

select platform.enable_tenant_vendor_rls('vendor', 'relationships', 'company_id');
