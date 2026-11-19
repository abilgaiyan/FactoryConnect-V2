-- RETIRED: immutable security grants cannot be updated in place.
-- Do not disable TR_Grant_Immutable or modify security.PermissionGrant directly.
-- This file is retained as a fail-closed placeholder to prevent rerunning the
-- unsupported local repair. Use the audited delegated-grant API instead.
THROW 51031, 'This direct grant repair is retired: immutable security authority. Use audited delegated grants.', 1;
