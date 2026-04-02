using Npgsql;
using NpgsqlTypes;

/// <summary>
/// Garante que os registros necessários existam em commercial.com_service
/// e retorna um dicionário chave → cs_id para uso na inserção das transações.
/// </summary>
internal static class ComServiceWriter
{
    public static string ServiceKey(int cmId, int dvId, DateTime dtOpen)
        => $"{cmId}|{dvId}|{dtOpen:yyyy-MM-dd}";

    public static async Task<Dictionary<string, long>> EnsureAsync(
        NpgsqlConnection pg, NpgsqlTransaction tx, List<SrcRecord> src, string blameUser, CancellationToken ct)
    {
        var keys = src
            .Select(s => new { cm = s.CmIdCalc!.Value, dv = s.DvIdCalc!.Value, open = s.CsDtOpenCalc, close = s.CsDtCloseCalc })
            .DistinctBy(k => (k.cm, k.dv, k.open))
            .ToList();

        const string ins = @"
            INSERT INTO commercial.com_service
                (cm_id, dv_id, cs_dtopen, cs_dtclose, cs_createdat, cs_createdby)
            VALUES
                (@cm_id, @dv_id, @cs_dtopen, @cs_dtclose, now(), @user)
            ON CONFLICT DO NOTHING;
            ";

        await using (var cmd = new NpgsqlCommand(ins, pg, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter("cm_id",     NpgsqlDbType.Integer));
            cmd.Parameters.Add(new NpgsqlParameter("dv_id",     NpgsqlDbType.Integer));
            cmd.Parameters.Add(new NpgsqlParameter("cs_dtopen", NpgsqlDbType.TimestampTz));
            cmd.Parameters.Add(new NpgsqlParameter("cs_dtclose",NpgsqlDbType.TimestampTz));
            cmd.Parameters.AddWithValue("user", blameUser);

            foreach (var k in keys)
            {
                cmd.Parameters["cm_id"].Value      = k.cm;
                cmd.Parameters["dv_id"].Value      = k.dv;
                cmd.Parameters["cs_dtopen"].Value  = k.open;
                cmd.Parameters["cs_dtclose"].Value = k.close;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        const string sel = @"
            SELECT cs_id::bigint
            FROM commercial.com_service
            WHERE cm_id = @cm_id AND dv_id = @dv_id AND cs_dtopen::date = @cs_dtopen::date
            LIMIT 1;
            ";

        var dict = new Dictionary<string, long>(StringComparer.Ordinal);

        await using (var cmd = new NpgsqlCommand(sel, pg, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter("cm_id",     NpgsqlDbType.Integer));
            cmd.Parameters.Add(new NpgsqlParameter("dv_id",     NpgsqlDbType.Integer));
            cmd.Parameters.Add(new NpgsqlParameter("cs_dtopen", NpgsqlDbType.TimestampTz));

            foreach (var k in keys)
            {
                cmd.Parameters["cm_id"].Value     = k.cm;
                cmd.Parameters["dv_id"].Value     = k.dv;
                cmd.Parameters["cs_dtopen"].Value = k.open;

                var csIdObj = await cmd.ExecuteScalarAsync(ct);
                if (csIdObj is null)
                    Console.WriteLine(
                        $"cs_id não encontrado após insert para {k.cm}|{k.dv}|{k.open:yyyy-MM-dd}");

                dict[ServiceKey(k.cm, k.dv, k.open)] = (long)csIdObj;
            }
        }

        return dict;
    }
}
