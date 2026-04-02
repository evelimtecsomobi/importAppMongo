using Npgsql;

/// <summary>
/// Filtra da lista de origem os registros que ainda não existem em commercial.com_tranmt.
/// </summary>
internal static class AntiJoinFilter
{
    public static async Task<List<SrcRecord>> FilterAsync(
        NpgsqlConnection pg, NpgsqlTransaction tx, List<SrcRecord> src, CancellationToken ct)
    {
        var ids = src.Select(s => s.RawObjectId).Distinct().ToArray();

        const string sql = @"
            SELECT ctm_legacy_id, cm_id::int, dv_id::int
            FROM commercial.com_tranmt
            WHERE ctm_legacy_id = ANY(@ids);
            ";

        var existing = new HashSet<(string id, int cm, int dv)>();

        await using var cmd = new NpgsqlCommand(sql, pg, tx);
        cmd.Parameters.AddWithValue("ids", ids);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            existing.Add((rd.GetString(0), rd.GetInt32(1), rd.GetInt32(2)));

        return src
            .Where(s => s.CmIdCalc is not null && s.DvIdCalc is not null)
            .Where(s => !existing.Contains((s.RawObjectId, s.CmIdCalc!.Value, s.DvIdCalc!.Value)))
            .ToList();
    }
}
