# Temporary character usage regression

Run from the repository root with .NET 10:

```powershell
dotnet run --project tests/SupportCharacterRegression -c Release
```

The executable invokes the production protobuf handlers, loads the local game's static tables and uses its own SQLite database and JSON save in the test output directory. It does not open the deployed player's database.

The current fixture reads the installed static pack under `../../Server and Server selector/cache`, matching the supplied 153.6.10 game configuration. Coverage includes normal/quick campaign battles, tower types, successful/failed/repeated settlements, selected squads, entry snapshots, exhausted quotas, separate content counters, unlimited events, daily rollover, save reload and legacy saves.

The original query regression was observed failing with an empty response before the fix. Additional regressions caught the missing quick-battle path, tower rollover ordering and quick-battle squad selection.
