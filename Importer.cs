using Npgsql;
using System.Diagnostics;

/// <summary>
/// Orquestrador principal do processo de importação.
/// Coordena leitura (MongoDB), filtros e escrita (Postgres) sem conhecer detalhes de cada etapa.
/// </summary>
public sealed class Importer(
    string mongoConnStr,
    string checkoutDatabase,
    string mobilityDatabase,
    string pgConnStr,
    string blameUser,
    int    cttId,
    int    mongoServerSelectionTimeoutSeconds = 30)
{
    public async Task RunAsync(DateTime startTs, DateTime endTs, bool forceImport = false, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"=== Início da importação {startTs:o} -> {endTs:o}, force={forceImport} ===");

        // --- 1. Leitura do MongoDB ---------------------------------------------------
        var reader = new MongoReader(mongoConnStr, checkoutDatabase, mobilityDatabase, mongoServerSelectionTimeoutSeconds);
        var srcAll = await reader.ReadAsync(startTs, endTs, ct);
        Console.WriteLine($"Leitura MongoDB concluída em {sw.Elapsed:mm\\:ss} ({srcAll.Count} registros lidos)");

        // --- 2. Filtro de canal ------------------------------------------------------
        srcAll = srcAll
            .Where(s => s.ChannelNorm is "TOP" or "WHATSAPP")
            .Where(s => s.CmIdCalc is not null && s.DvIdCalc is not null)
            .ToList();

        Console.WriteLine($"Após filtro de canal: {srcAll.Count} registros");

        if (srcAll.Count == 0)
        {
            Console.WriteLine("Sem registros na janela informada.");
            return;
        }

        // --- 3. Anti-join + com_service (transação compartilhada) --------------------
        await using var pg = new NpgsqlConnection(pgConnStr);
        await pg.OpenAsync(ct);
        await using var tx = await pg.BeginTransactionAsync(ct);

        List<SrcRecord> toImport;

        if (forceImport)
        {
            Console.WriteLine("Forçando import (anti-join ignorado)");
            toImport = srcAll;
        }
        else
        {
            Console.WriteLine("Verificando registros existentes no Postgres (anti-join)...");
            toImport = await AntiJoinFilter.FilterAsync(pg, tx, srcAll, ct);
            Console.WriteLine($"Registros a importar após anti-join: {toImport.Count}");

            if (toImport.Count == 0)
            {
                Console.WriteLine("Todos os registros já existiam no Postgres.");
                await tx.CommitAsync(ct);
                return;
            }
        }

        Console.WriteLine("Criando serviços (com_service)...");
        var sw2 = Stopwatch.StartNew();
        var serviceIdByKey = await ComServiceWriter.EnsureAsync(pg, tx, toImport, blameUser, ct);
        Console.WriteLine($"Serviços garantidos em {sw2.Elapsed:mm\\:ss}");

        await tx.CommitAsync(ct);

        // --- 4. Inserção paralela por worker -----------------------------------------
        Console.WriteLine("Iniciando inserção paralela...");
        var sw3 = Stopwatch.StartNew();

        var writer     = new ComTransmtWriter(pgConnStr, blameUser, cttId);
        int workerCount = Math.Min(Environment.ProcessorCount, toImport.Count);
        int chunkSize   = (toImport.Count + workerCount - 1) / workerCount;
        var tasks       = new List<Task<int>>(workerCount);

        for (int w = 0; w < workerCount; w++)
        {
            var chunk = toImport.Skip(w * chunkSize).Take(chunkSize).ToList();
            if (chunk.Count == 0) continue;
            tasks.Add(Task.Run(() => writer.InsertAsync(chunk, serviceIdByKey, ct)));
        }

        var inserted = (await Task.WhenAll(tasks)).Sum();

        Console.WriteLine($"Inserção paralela concluída em {sw3.Elapsed:mm\\:ss} (workers={workerCount})");
        Console.WriteLine($"Import concluído. Inseridos: {inserted} transações. Tempo total {sw.Elapsed:mm\\:ss}");
    }
}
