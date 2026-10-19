using Microsoft.Data.Sqlite;

namespace AgentStateDaemon.TestSupport;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 3 || args[0] != "--hold-uncommitted")
        {
            return 2;
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = args[1],
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE accounts SET nickname='SYNTHETIC_UNCOMMITTED_ONLY', revision='synthetic-uncommitted-revision' WHERE account_id=$account_id;";
            command.Parameters.AddWithValue("$account_id", args[2]);
            if (command.ExecuteNonQuery() != 1)
            {
                return 3;
            }
        }

        Console.Out.WriteLine("READY");
        Console.Out.Flush();
        Console.In.ReadLine();
        return 0;
    }
}
