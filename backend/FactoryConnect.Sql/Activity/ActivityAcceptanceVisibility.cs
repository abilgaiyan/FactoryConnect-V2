using System.Runtime.CompilerServices;

// Disposable SQL acceptance may exercise the unqualified storage fixture.
// Production callers must enter through the evidence-bound ApplyAcceptedAsync API.
[assembly: InternalsVisibleTo("FactoryConnect.Sql.Acceptance")]
