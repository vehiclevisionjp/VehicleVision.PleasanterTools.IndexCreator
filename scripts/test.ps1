param([ValidateSet('SQLServer','PostgreSQL','MySQL')][string]$Dbms, [switch]$Benchmark)
$ErrorActionPreference = 'Stop'
dotnet run --project tests/IndexCreator.Tests -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Unit checks failed.' }
if (-not $Dbms) { exit 0 }
$profile = switch ($Dbms) { 'SQLServer' { 'sqlserver' }; 'PostgreSQL' { 'postgres' }; 'MySQL' { 'mysql' } }
docker compose --profile $profile up -d --wait
if ($LASTEXITCODE -ne 0) { throw 'Database startup failed.' }
if ($Dbms -eq 'SQLServer') {
    docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P IndexCreator_test_only_123 -C -b -Q "IF DB_ID('IndexCreatorTest') IS NULL CREATE DATABASE IndexCreatorTest;"
    if ($LASTEXITCODE -ne 0) { throw 'Test database creation failed.' }
}
$previousDbms = $env:INDEXCREATOR_TEST_DBMS
$previousConnection = $env:INDEXCREATOR_TEST_CONNECTION
try {
    $env:INDEXCREATOR_TEST_DBMS = $Dbms
    $env:INDEXCREATOR_TEST_CONNECTION = switch ($Dbms) {
        'SQLServer' { 'Server=127.0.0.1,51433;Database=IndexCreatorTest;User ID=sa;Password=IndexCreator_test_only_123;TrustServerCertificate=true' }
        'PostgreSQL' { 'Host=localhost;Port=55432;Database=IndexCreatorTest;Username=indexcreator;Password=IndexCreator_test_only_123' }
        'MySQL' { 'Server=localhost;Port=53306;Database=IndexCreatorTest;User ID=root;Password=IndexCreator_test_only_123' }
    }
    $arguments = @('--integration')
    if ($Benchmark) { $arguments += '--benchmark' }
    dotnet run --project tests/IndexCreator.Tests -c Release --no-restore -- @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Integration checks failed.' }
} finally {
    $env:INDEXCREATOR_TEST_DBMS = $previousDbms
    $env:INDEXCREATOR_TEST_CONNECTION = $previousConnection
}
