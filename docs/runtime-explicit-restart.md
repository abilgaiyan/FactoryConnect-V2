# Explicit runtime restart for configuration activation

`Start-FactoryConnectRuntime.ps1 -InstallRoot D:\FactoryConnect -StartupMode Restart`
explicitly restarts the selected release's Edge, API and Dashboard, including
when all three are healthy. Default Reconcile and BootSafe behavior is unchanged.

Use this after backing up and reviewing the commissioned configuration files.
This is a whole application runtime restart: acquisition pauses while Edge is
down, HTTP requests may fail, and no lossless recovery is promised. Separate
FANUC/MOXA adapters and MTConnect agents are not stopped. The existing migration,
release selection and SQL contracts are unchanged; this command runs no migration.

Restart retains the deployment lock, release-junction validation, commissioned
configuration checks, exact PID/path/start-time ownership, bounded termination,
launch intent, atomic runtime records, cleanup and health verification. Like
BootSafe, it first inventories all runtime processes and rejects unrecorded
processes or inventory failures before stopping anything. Unlike BootSafe, it
permits reconciliation of known partial/unhealthy state. An existing launch
intent or ownership mismatch still blocks execution.

Deploy the reviewed script with its matching `FactoryConnect.ProcessTermination.ps1`
helper in an operations directory; it resolves executables through InstallRoot's
current junction, not its own directory. Do not edit immutable release files.
Do not run alongside a deployment or another restart. There is no configuration
rollback: restore the saved configuration and use the same restart operation if
activation fails, after resolving any reported ownership or launch-intent issue.

Validation: `tests/deployment/Invoke-BootSafeRuntimeConformance.ps1` exercises
healthy restart, cold startup, partial/unhealthy reconciliation, ownership
mismatch, duplicate/unrecorded inventory and inventory failure with controlled
process/health outcomes. Actual Windows process launch and factory acceptance
must be verified separately.
