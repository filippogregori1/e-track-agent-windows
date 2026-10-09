using ActivityTracker.Checks;

// Verifiche della logica Core senza framework di test, come `ActivityTrackerChecks` sul Mac:
// esce con codice 1 se una verifica fallisce.

CoreChecks.Run();
SyncChecks.Run();
FakeEquipeTrack.RunChecks();

Console.WriteLine($"\n{H.Passes} verifiche superate, {H.Failures} fallite");
return H.Failures > 0 ? 1 : 0;
