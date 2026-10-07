using System.Data.Common;
using System.Diagnostics;
using VehicleVision.PleasanterTools.IndexCreator;

internal static class Benchmark
{
    public static async Task Run(Configuration config, Database database, SqlDialect d)
    {
        var digits = "(" + string.Join(" UNION ALL ", Enumerable.Range(0, 10).Select(n => "SELECT " + n + " AS n")) + ")";
        var number = "a.n+10*b.n+100*c.n+1000*d.n+10000*e.n";
        var cast = config.Dbms == Dbms.MySQL ? "CHAR" : "varchar(32)";
        await database.Execute($"INSERT INTO {d.Table("Results")} ({d.Quote("SiteId")},{d.Quote("ResultId")},{d.Quote("UpdatedTime")},{d.Quote("ClassA")},{d.Quote("Status")}) SELECT 1,100+({number}),'2026-01-01',CAST(({number})%100 AS {cast}),100 FROM {digits} a CROSS JOIN {digits} b CROSS JOIN {digits} c CROSS JOIN {digits} d CROSS JOIN {digits} e");
        await using DbConnection query = config.Dbms switch { Dbms.SQLServer => new Microsoft.Data.SqlClient.SqlConnection(config.ConnectionString), Dbms.PostgreSQL => new Npgsql.NpgsqlConnection(config.ConnectionString), _ => new MySqlConnector.MySqlConnection(config.ConnectionString) };
        await query.OpenAsync();
        async Task<double> Measure()
        {
            await using var cmd = query.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {d.Table("Results")} WHERE {d.Quote("SiteId")}=1 AND {d.Quote("ClassA")}='42'";
            var times = new List<double>();
            for (var i = 0; i < 6; i++)
            {
                var watch = Stopwatch.StartNew();
                var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                watch.Stop();
                if (count != 1000) throw new Exception("Benchmark result mismatch.");
                if (i > 0) times.Add(watch.Elapsed.TotalMilliseconds);
            }
            return times.Order().ElementAt(2);
        }
        var before = await Measure();
        var spec = new IndexSpec("Results", [new("SiteId"), new("ClassA", Prefix: config.Dbms == Dbms.MySQL ? 100 : 0)]);
        await database.Apply(Reconciler.Plan([spec], await database.ReadIndexes(default), false), default);
        var after = await Measure();
        Console.WriteLine(FormattableString.Invariant($"Benchmark ({config.Dbms}): rows=100000, matches=1000, median before={before:F3}ms, after={after:F3}ms. Synthetic data only; no latency threshold asserted."));
    }
}
