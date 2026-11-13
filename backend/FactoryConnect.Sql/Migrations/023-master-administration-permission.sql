-- Extend the supported permission vocabulary for site, line and machine administration.
-- Schema-only migration: existing grants, principals and bootstrap history remain unchanged.
ALTER TABLE security.PermissionGrant DROP CONSTRAINT CK_Grant_Permission;
ALTER TABLE security.PermissionGrant WITH CHECK ADD CONSTRAINT CK_Grant_Permission
CHECK (Permission IN (
    'security.access.manage',
    'configuration.read',
    'configuration.edit',
    'configuration.submit',
    'configuration.review',
    'configuration.activate',
    'configuration.master.manage',
    'commissioning.read',
    'audit.read',
    'machine.suspend',
    'machine.enable',
    'policy.revoke',
    'runtime.assign',
    'runtime.credential.manage'
));
