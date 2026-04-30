using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

/// <summary>
/// Insere um chunk de registros nas tabelas:
///   com_tranmt               (via COPY binário)
///   com_trandt               (detalhe por transação)
///   com_trandt_medias        (mídias do detalhe)
///   com_tranmt_x_payment_mode (forma de pagamento)
/// Cada chamada roda dentro de sua própria transação.
/// </summary>
internal sealed class ComTransmtWriter(string pgConnStr, string blameUser, int cttId)
{
    public async Task<int> InsertAsync(
        List<SrcRecord> src,
        Dictionary<string, long> serviceIdByKey,
        CancellationToken ct)
    {
        await using var pg = new NpgsqlConnection(pgConnStr);
        await pg.OpenAsync(ct);
        await using var tx = await pg.BeginTransactionAsync(ct);

        CopyTransmtAsync(pg, src, serviceIdByKey);
        var extToCtm = await LoadCtmIdsAsync(pg, tx, src, ct);
        await InsertDetailAsync(pg, tx, src, extToCtm, ct);

        await tx.CommitAsync(ct);
        return src.Count;
    }

    // -------------------------------------------------------------------------
    // COPY binário → com_tranmt
    // -------------------------------------------------------------------------
    private void CopyTransmtAsync(NpgsqlConnection pg, List<SrcRecord> src, Dictionary<string, long> serviceIdByKey)
    {
        const string sql = @"
            COPY commercial.com_tranmt (
                cm_id, ctt_id, cs_id, dv_id,
                ctm_datetime_tz, ctm_confirm_datetime_tz,
                ctm_value, ctm_receivedvalue,
                ctm_externalid, ctm_status,
                ctm_createdby, ctm_createdat, ctm_legacy_id
            ) FROM STDIN (FORMAT BINARY)";

        using var importer = pg.BeginBinaryImport(sql);

        foreach (var s in src)
        {
            var cm = s.CmIdCalc!.Value;
            var dv = s.DvIdCalc!.Value;
            var sk = ComServiceWriter.ServiceKey(cm, dv, s.CsDtOpenCalc);

            if (!serviceIdByKey.TryGetValue(sk, out var csId))
                throw new InvalidOperationException($"cs_id não encontrado para key {sk}");

            importer.StartRow();
            importer.Write(cm,                                  NpgsqlDbType.Integer);
            importer.Write((short)cttId,                        NpgsqlDbType.Smallint);
            importer.Write(csId,                                NpgsqlDbType.Bigint);
            importer.Write(dv,                                  NpgsqlDbType.Integer);
            importer.Write(s.CreateDateSbe, NpgsqlDbType.TimestampTz);
            importer.Write(s.ConfirmDate,   NpgsqlDbType.TimestampTz);
            importer.Write(s.TotalValue,                        NpgsqlDbType.Numeric);
            importer.Write(s.ReceivedValue,                     NpgsqlDbType.Numeric);
            importer.Write(s.ExternalId,                        NpgsqlDbType.Bigint);
            importer.Write("A",                                 NpgsqlDbType.Varchar);
            importer.Write(blameUser,                           NpgsqlDbType.Varchar);
            importer.Write(DateTime.UtcNow,                     NpgsqlDbType.TimestampTz);
            importer.Write(s.RawObjectId,                       NpgsqlDbType.Varchar);
        }

        importer.Complete();
    }

    // -------------------------------------------------------------------------
    // Lê os ctm_id gerados pelo COPY
    // -------------------------------------------------------------------------
    private static async Task<Dictionary<string, long>> LoadCtmIdsAsync(
        NpgsqlConnection pg, NpgsqlTransaction tx, List<SrcRecord> src, CancellationToken ct)
    {
        var ids = src.Select(s => s.RawObjectId).Distinct().ToArray();

        const string sql = @"
            SELECT ctm_legacy_id, ctm_id::bigint
            FROM commercial.com_tranmt
            WHERE ctm_legacy_id = ANY(@ids);
            ";

        var dict = new Dictionary<string, long>(ids.Length);

        await using var cmd = new NpgsqlCommand(sql, pg, tx);
        cmd.Parameters.AddWithValue("ids", ids);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            dict[rd.GetString(0)] = rd.GetInt64(1);

        return dict;
    }

    // -------------------------------------------------------------------------
    // com_trandt + com_trandt_medias + com_tranmt_x_payment_mode
    // -------------------------------------------------------------------------
    private async Task InsertDetailAsync(
        NpgsqlConnection pg, NpgsqlTransaction tx,
        List<SrcRecord> src, Dictionary<string, long> extToCtm, CancellationToken ct)
    {
        const string insDt = @"
            INSERT INTO commercial.com_trandt (
                ctm_id, ctd_value, ctd_quantity, ctd_mediatype, cp_id,
                ctd_createdat, ctd_createdby
            ) VALUES (
                @ctm_id, @ctd_value, @ctd_quantity, @ctd_mediatype, @cp_id,
                @createdat, @user
            )
            RETURNING ctd_id::int;
            ";

        const string insMed = @"
            INSERT INTO commercial.com_trandt_medias (
                ctd_id, ctdm_mediatype, ctdm_mediaid
            ) VALUES (
                @ctd_id, @ctdm_mediatype, @ctdm_mediaid
            )
            ON CONFLICT DO NOTHING;
            ";

        const string insPm = @"
            INSERT INTO commercial.com_tranmt_x_payment_mode (
                ctm_id, cpm_id, ctmxcpm_status,
                ctmxcpm_dtpayment, ctmxcpm_createdat, ctmxcpm_createdby,
                ctmxcpm_value, ctmxcpm_acquirer_name,
                ctmxcpm_pix_txid, ctmxcpm_pix_endtoendid,
                ctmxcpm_nsu_acquirer, ctmxcpm_aut_acquirer, ctmxcpm_brand
            ) VALUES (
                @ctm_id, @cpm_id, 'A',
                @dtpayment, now(), @user,
                @value, @acquirer_name,
                @pix_txid, @pix_endtoendid,
                @nsu_acquirer, @aut_acquirer, @brand
            );
            ";

        await using var cmdDt = new NpgsqlCommand(insDt, pg, tx);
        cmdDt.Parameters.Add(new NpgsqlParameter("ctm_id",       NpgsqlDbType.Bigint));
        cmdDt.Parameters.Add(new NpgsqlParameter("ctd_value",    NpgsqlDbType.Numeric));
        cmdDt.Parameters.Add(new NpgsqlParameter("ctd_quantity", NpgsqlDbType.Integer));
        cmdDt.Parameters.Add(new NpgsqlParameter("ctd_mediatype", NpgsqlDbType.Integer) { IsNullable = true });
        cmdDt.Parameters.Add(new NpgsqlParameter("cp_id",        NpgsqlDbType.Integer));
        cmdDt.Parameters.Add(new NpgsqlParameter("createdat",    NpgsqlDbType.TimestampTz));
        cmdDt.Parameters.AddWithValue("user", blameUser);

        await using var cmdMed = new NpgsqlCommand(insMed, pg, tx);
        cmdMed.Parameters.Add(new NpgsqlParameter("ctd_id",        NpgsqlDbType.Integer));
        cmdMed.Parameters.Add(new NpgsqlParameter("ctdm_mediatype",NpgsqlDbType.Integer));
        cmdMed.Parameters.Add(new NpgsqlParameter("ctdm_mediaid",  NpgsqlDbType.Bigint));

        await using var cmdPm = new NpgsqlCommand(insPm, pg, tx);
        cmdPm.Parameters.Add(new NpgsqlParameter("ctm_id",        NpgsqlDbType.Bigint));
        cmdPm.Parameters.Add(new NpgsqlParameter("cpm_id",        NpgsqlDbType.Integer));
        cmdPm.Parameters.Add(new NpgsqlParameter("dtpayment",     NpgsqlDbType.TimestampTz));
        cmdPm.Parameters.Add(new NpgsqlParameter("value",         NpgsqlDbType.Numeric));
        cmdPm.Parameters.Add(new NpgsqlParameter("acquirer_name", NpgsqlDbType.Varchar));
        cmdPm.Parameters.Add(new NpgsqlParameter("pix_txid",      NpgsqlDbType.Varchar));
        cmdPm.Parameters.Add(new NpgsqlParameter("pix_endtoendid",NpgsqlDbType.Char));
        cmdPm.Parameters.Add(new NpgsqlParameter("nsu_acquirer",  NpgsqlDbType.Bigint));
        cmdPm.Parameters.Add(new NpgsqlParameter("aut_acquirer",  NpgsqlDbType.Varchar));
        cmdPm.Parameters.Add(new NpgsqlParameter("brand",         NpgsqlDbType.Varchar));
        cmdPm.Parameters.AddWithValue("user", blameUser);

        foreach (var s in src)
        {
            if (!extToCtm.TryGetValue(s.RawObjectId, out var ctmId))
                throw new InvalidOperationException(
                    $"ctm_id não encontrado após COPY para legacy_id={s.RawObjectId}");

            int ctdId;

            if (s.IsCoupon)
            {
                // COUPON: insere apenas a linha de cupom (cp_id=745, mediatype=null)
                cmdDt.Parameters["ctm_id"].Value        = ctmId;
                cmdDt.Parameters["ctd_value"].Value     = s.CouponValue;
                cmdDt.Parameters["ctd_quantity"].Value  = 1;
                cmdDt.Parameters["ctd_mediatype"].Value = DBNull.Value;
                cmdDt.Parameters["cp_id"].Value         = 745;
                cmdDt.Parameters["createdat"].Value     = s.CreateDateSbe;

                ctdId = (int)(await cmdDt.ExecuteScalarAsync(ct)
                    ?? throw new InvalidOperationException(
                        $"Falha ao inserir com_trandt (COUPON) para externalid={s.ExternalId} objectid={s.RawObjectId}"));
            }
            else
            {
                var quantity = s.MediaIds.Length > 0 ? s.MediaIds.Length : 1;

                // com_trandt principal
                cmdDt.Parameters["ctm_id"].Value        = ctmId;
                cmdDt.Parameters["ctd_value"].Value     = s.Value / quantity;
                cmdDt.Parameters["ctd_quantity"].Value  = quantity;
                cmdDt.Parameters["ctd_mediatype"].Value = (object?)s.MediaType ?? DBNull.Value;
                cmdDt.Parameters["cp_id"].Value         = s.CpIdCalc;
                cmdDt.Parameters["createdat"].Value     = s.CreateDateSbe;

                ctdId = (int)(await cmdDt.ExecuteScalarAsync(ct)
                    ?? throw new InvalidOperationException(
                        $"Falha ao inserir com_trandt (ctd_id null) para externalid={s.ExternalId} objectid={s.RawObjectId}"));

                // com_trandt — cupom adicional (quando couponValue > 0)
                if (s.CouponValue > 0)
                {
                    cmdDt.Parameters["ctm_id"].Value        = ctmId;
                    cmdDt.Parameters["ctd_value"].Value     = s.CouponValue;
                    cmdDt.Parameters["ctd_quantity"].Value  = 1;
                    cmdDt.Parameters["ctd_mediatype"].Value = DBNull.Value;
                    cmdDt.Parameters["cp_id"].Value         = 745;
                    cmdDt.Parameters["createdat"].Value     = s.CreateDateSbe;
                    await cmdDt.ExecuteScalarAsync(ct); // ctd_id do cupom não é usado
                }
            }

            // com_trandt_medias
            cmdMed.Parameters["ctd_id"].Value         = ctdId;
            cmdMed.Parameters["ctdm_mediatype"].Value = (object?)s.MediaType ?? DBNull.Value;

            foreach (var mediaId in s.MediaIds)
            {
                cmdMed.Parameters["ctdm_mediaid"].Value = mediaId;
                await cmdMed.ExecuteNonQueryAsync(ct);
            }

            // com_tranmt_x_payment_mode
            if (s.CpmIdCalc is not null)
            {
                cmdPm.Parameters["ctm_id"].Value         = ctmId;
                cmdPm.Parameters["cpm_id"].Value         = s.CpmIdCalc.Value;
                cmdPm.Parameters["dtpayment"].Value      = s.ConfirmDate;
                cmdPm.Parameters["value"].Value          = s.TotalValue;
                cmdPm.Parameters["acquirer_name"].Value  = (object?)s.AcquirerName          ?? DBNull.Value;
                cmdPm.Parameters["pix_txid"].Value       = s.AcquirerTransactionId is not null
                    ? Regex.Replace(s.AcquirerTransactionId, @"[^a-zA-Z0-9]", "")
                    : DBNull.Value;
                cmdPm.Parameters["pix_endtoendid"].Value = (object?)s.AcquirerPaymentId     ?? DBNull.Value;
                cmdPm.Parameters["nsu_acquirer"].Value   = 1L;
                cmdPm.Parameters["aut_acquirer"].Value   = (object?)s.AcquirerNsu            ?? DBNull.Value;
                cmdPm.Parameters["brand"].Value          = (object?)s.Brand                  ?? DBNull.Value;

                try
                {
                    await cmdPm.ExecuteNonQueryAsync(ct);
                }
                catch (Exception ex)
                {
                    var paramDump = string.Join(", ", cmdPm.Parameters.Cast<NpgsqlParameter>()
                        .Select(p => $"{p.ParameterName}={p.Value}"));
                    Console.Error.WriteLine($"[cmdPm ERROR] externalid={s.ExternalId} objectid={s.RawObjectId} | {paramDump}");
                    Console.Error.WriteLine($"[cmdPm ERROR] exception: {ex.Message}");
                    throw;
                }
            }
        }
    }
}
