using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// Lê orders da collection autopass-checkout-service.orders (TICKET e RECHARGE)
/// e resolve os MediaIds via join com mobility.qrcode (apenas para TICKET).
/// </summary>
internal sealed class MongoReader(string mongoConnStr, string checkoutDatabase, string mobilityDatabase, int serverSelectionTimeoutSeconds = 30)
{
    private static readonly string[] CompletedStates = ["COMPLETED", "ACCEPTED"];
    public async Task<List<SrcRecord>> ReadAsync(DateTime startTs, DateTime endTs, CancellationToken ct)
    {
        var settings = MongoClientSettings.FromConnectionString(mongoConnStr);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(serverSelectionTimeoutSeconds);
        var client  = new MongoClient(settings);
        var records = await ReadOrdersAsync(client, startTs, endTs, ct);

        Console.WriteLine($"  Tickets lidos:  {records.Count(r => r.IsTicket)}");
        Console.WriteLine($"  Recargas lidas: {records.Count(r => r.IsRecharge)}");

        return records;
    }

    // -------------------------------------------------------------------------
    // Orders: autopass-checkout-service.orders
    // Origem única para TICKET e RECHARGE.
    //
    // TICKET:
    //   MediaIds = qrcode.externalTransactionId (join orders.items[] → qrcode._id)
    //   PaidDate = stateEvents[PAYMENT_AUTHORIZED].createdAt
    //
    // RECHARGE:
    //   MediaIds = cardNumber (long)
    //   PaidDate = stateEvents[COMPLETED].createdAt
    // -------------------------------------------------------------------------
    private async Task<List<SrcRecord>> ReadOrdersAsync(
        MongoClient client, DateTime startTs, DateTime endTs, CancellationToken ct)
    {
        var ordersCol = client
            .GetDatabase(checkoutDatabase)
            .GetCollection<BsonDocument>("orders");

        var qrcodeCol = client
            .GetDatabase(mobilityDatabase)
            .GetCollection<BsonDocument>("qrcode");

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Gte("createdAt", startTs.ToUniversalTime()),
            Builders<BsonDocument>.Filter.Lt("createdAt",  endTs.ToUniversalTime()),
            Builders<BsonDocument>.Filter.In("state", CompletedStates)
            //,Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse("690559bf73775b1df78bacd9"))
        );

        var orders = await ordersCol.Find(filter).ToListAsync(ct);
         Console.WriteLine($"  [INFO] orders.Count {orders.Count} no início!!!");

         //throw new Exception();

        if (orders.Count == 0)
            return new List<SrcRecord>();

        // Coleta todos os item-ObjectIds de ordens TICKET para busca em lote no qrcode
        var allItemOids = orders
            .Where(o => (o["product"].AsString == "TICKET" || o["product"].AsString == "TICKET_COUPON")
                     && o.Contains("items") && o["items"].IsBsonArray)
            .SelectMany(o => o["items"].AsBsonArray
                .Select(i => i.AsString)
                .Where(s => ObjectId.TryParse(s, out _))
                .Select(s => ObjectId.Parse(s)))
            .Distinct()
            .ToList();

        // Busca em lote: qrcode._id → externalTransactionId (chunks de 500 para respeitar limite 16MB do MongoDB)
        const int chunkSize = 500;
        var extIdByItemOid = new Dictionary<string, long>(allItemOids.Count);
        for (int i = 0; i < allItemOids.Count; i += chunkSize)
        {
            var chunk    = allItemOids.GetRange(i, Math.Min(chunkSize, allItemOids.Count - i));
            var qrFilter = Builders<BsonDocument>.Filter.In("_id", chunk);
            var qrcodes  = await qrcodeCol
                .Find(qrFilter)
                .Project(Builders<BsonDocument>.Projection.Include("_id").Include("externalTransactionId"))
                .ToListAsync(ct);

            foreach (var qr in qrcodes)
            {
                var idStr = qr["_id"].AsObjectId.ToString();
                if (qr.Contains("externalTransactionId") && !qr["externalTransactionId"].IsBsonNull)
                {
                    if (long.TryParse(qr["externalTransactionId"].AsString, out var extId) && extId != 0)
                        extIdByItemOid[idStr] = extId;
                }
            }
        }

        var records = new List<SrcRecord>(orders.Count);

        foreach (var doc in orders)
        {
            var oid     = doc["_id"].AsObjectId.ToString();
            var rawProduct = doc["product"].AsString;
            var product = rawProduct switch
            {
                "TICKET_COUPON"   => "TICKET",
                "RECHARGE_COUPON" => "RECHARGE",
                _                 => rawProduct
            };

            string? channel = GetStr(doc, "channel");
            var createdAt   = doc["createdAt"].ToUniversalTime();

            // PaidDate: TICKET → PAYMENT_AUTHORIZED; RECHARGE → COMPLETED
            DateTime? paidDate = null;
            if (doc.Contains("stateEvents") && doc["stateEvents"].IsBsonArray)
            {
                var targetState = product == "TICKET" ? "PAYMENT_AUTHORIZED" : "COMPLETED";
                var ev = doc["stateEvents"].AsBsonArray
                    .Select(e => e.AsBsonDocument)
                    .FirstOrDefault(e => e.Contains("state") && e["state"].AsString == targetState);
                if (ev is not null && ev.Contains("createdAt") && !ev["createdAt"].IsBsonNull)
                    paidDate = ev["createdAt"].ToUniversalTime();
            }

            decimal value, totalValue, couponValue;
            try
            {
                value       = GetDecimal(doc, "value");
                totalValue  = doc.Contains("totalValue") ? GetDecimal(doc, "totalValue") : value;
                couponValue = doc.Contains("couponValue") ? GetDecimal(doc, "couponValue") : 0m;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [ERRO] _id={oid} product={rawProduct} createdAt={createdAt:O}");
                Console.WriteLine($"         campos: value={doc.Contains("value")} totalValue={doc.Contains("totalValue")} couponValue={doc.Contains("couponValue")}");
                Console.WriteLine($"         doc: {doc.ToJson()}");
                Console.WriteLine($"         ex: {ex.Message}");
                throw;
            }

            string? paymentType = null;
            if (doc.Contains("payment") && !doc["payment"].IsBsonNull)
            {
                var payDoc  = doc["payment"].AsBsonDocument;
                paymentType = GetStr(payDoc, "type");
            }

            BsonDocument? pd = doc.Contains("paymentData") && !doc["paymentData"].IsBsonNull
                ? doc["paymentData"].AsBsonDocument : null;

            bool isPefisa = paymentType is not null &&
                            paymentType.Equals("BALANCE", StringComparison.OrdinalIgnoreCase);
            bool isPix    = paymentType is not null &&
                            paymentType.Equals("PIX", StringComparison.OrdinalIgnoreCase);

            string? brand        = isPefisa ? "PEFISA" : isPix ? GetStr(pd, "pixProvider") : GetStr(pd, "brand");
            string? acquirerName = isPefisa ? "PEFISA" : isPix ? GetStr(pd, "pixProvider") : GetStr(pd, "acquirerType");
            string? acquirerNsu  = GetStr(pd, "acquirerNsu");
            string? acquirerTxId;
            string? acquirerPayId;
            long[]  mediaIds;
            string? cardSessionId = null;

            if (product == "TICKET")
            {
                acquirerTxId  = GetStr(pd, "acquirerId") ?? GetStr(pd, "instantPaymentId");
                acquirerPayId = GetStr(pd, "endToEndId");

                var itemOids = doc.Contains("items") && doc["items"].IsBsonArray
                    ? doc["items"].AsBsonArray.Select(i => i.AsString).ToArray()
                    : Array.Empty<string>();

                mediaIds = itemOids
                    .Where(id => extIdByItemOid.ContainsKey(id))
                    .Select(id => extIdByItemOid[id])
                    .Distinct()
                    .ToArray();

                // if (mediaIds.Length == 0)
                //     Console.WriteLine($"  [INFO] Order {oid} (TICKET) sem externalTransactionId válidos — importado sem mídia.");
            }
            else // RECHARGE
            {
                acquirerTxId  = GetStr(pd, "instantPaymentId");
                acquirerPayId = GetStr(pd, "endToEndId");

                if (!doc.Contains("cardNumber") || doc["cardNumber"].IsBsonNull)
                {
                    //Console.WriteLine($"  [INFO] Order {oid} (RECHARGE) sem cardNumber — importado sem mídia.");
                    mediaIds = [];
                }
                else
                {
                    var cardNum   = long.Parse(doc["cardNumber"].AsString);
                    mediaIds      = [cardNum];
                    cardSessionId = doc.Contains("cardSession") && !doc["cardSession"].IsBsonNull
                        ? doc["cardSession"].AsInt64.ToString() : null;
                }
            }

            records.Add(new SrcRecord(
                ExternalId:            SrcRecord.ObjectIdToLong(oid),
                RawObjectId:           oid,
                IssuerType:            product,
                Channel:               channel,
                CreateDateSbe:         createdAt,
                PaidDate:              paidDate,
                Value:                 value,
                TotalValue:            totalValue,
                CouponValue:           couponValue,
                PaymentType:           paymentType,
                Brand:                 brand,
                AcquirerName:          acquirerName,
                AcquirerNsu:           acquirerNsu,
                AcquirerTransactionId: acquirerTxId,
                AcquirerPaymentId:     acquirerPayId,
                MediaIds:              mediaIds,
                CardSessionId:         cardSessionId
            ));
        }

        return records;
    }

    private static string? GetStr(BsonDocument? doc, string field)
        => doc is not null && doc.Contains(field) && !doc[field].IsBsonNull
            ? doc[field].AsString : null;

    private static decimal GetDecimal(BsonDocument doc, string field)
    {
        var v = doc[field];
        return v.BsonType switch
        {
            BsonType.Decimal128 => (decimal)v.AsDecimal128,
            BsonType.Double     => (decimal)v.AsDouble,
            BsonType.Int32      => v.AsInt32,
            BsonType.Int64      => v.AsInt64,
            BsonType.String     => decimal.Parse(v.AsString, System.Globalization.CultureInfo.InvariantCulture),
            _                   => throw new InvalidCastException($"Campo '{field}' tem tipo inesperado: {v.BsonType}")
        };
    }
}
