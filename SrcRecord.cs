/// <summary>
/// Modelo unificado que representa um registro de origem lido do MongoDB.
/// Origem: autopass-checkout-service.orders (product = TICKET | RECHARGE)
/// </summary>
public sealed record SrcRecord(
    // --- Identificação ---
    long   ExternalId,          // ObjectId hex → long (primeiros 8 bytes)
    string RawObjectId,         // _id.$oid original, usado como ctm_legacy_id

    // --- Tipo de produto ---
    string IssuerType,          // "TICKET" | "RECHARGE"  (orders.product)

    // --- Canal ---
    // Campo confirmado: orders.channel
    // Valores conhecidos: "TOP", "WHATSAPP"
    string? Channel,

    // --- Datas ---
    DateTime  CreateDateSbe,    // orders.createdAt
    DateTime? PaidDate,         // TICKET: stateEvents[PAYMENT_AUTHORIZED].createdAt
                                // RECHARGE: stateEvents[COMPLETED].createdAt

    // --- Valores ---
    decimal Value,              // orders.value       → ctd_value (valor do produto)
    decimal TotalValue,         // orders.totalValue  → ctm_value, ctm_receivedvalue, ctmxcpm_value
    decimal CouponValue,        // orders.couponValue → com_trandt extra (cp_id=745) quando > 0

    // --- Pagamento ---
    string? PaymentType,        // orders.payment.type  (ex: "DEBIT", "CREDIT", "PIX")
    string? Brand,              // orders.paymentData.brand  (ex: "MAESTRO", "VISA")

    // --- Adquirente ---
    string? AcquirerName,       // orders.paymentData.acquirerType  (ex: "CIELO")
    string? AcquirerNsu,        // orders.paymentData.acquirerNsu   → ctmxcpm_aut_acquirer
    string? AcquirerTransactionId, // TICKET: paymentData.acquirerId
                                   // RECHARGE: paymentData.instantPaymentId
    string? AcquirerPaymentId,  // RECHARGE: paymentData.endToEndId → ctmxcpm_pix_endtoendid

    // --- Mídias ---
    // TICKET:   externalTransactionId dos qrcodes (join orders.items[] → mobility.qrcode._id)
    // RECHARGE: cardNumber (long)
    long[] MediaIds,

    // --- Recarga ---
    string? CardSessionId       // orders.cardSession (informativo)
)
{
    // -------------------------------------------------------------------------
    // Canal normalizado
    // -------------------------------------------------------------------------
    public string? ChannelNorm => Channel is null ? null
        : Channel.Contains("TOP",      StringComparison.OrdinalIgnoreCase) ? "TOP"
        : Channel.Contains("WHATSAPP", StringComparison.OrdinalIgnoreCase) ? "WHATSAPP"
        : null;

    public int? CmIdCalc => ChannelNorm switch
    {
        "TOP"      => 20,
        "WHATSAPP" => 79,
        _          => null
    };

    public int? DvIdCalc => ChannelNorm switch
    {
        "TOP"      => 100000,
        "WHATSAPP" => 100001,
        _          => null
    };

    // -------------------------------------------------------------------------
    // Datas calculadas para com_service
    // -------------------------------------------------------------------------
    public DateTime CsDtOpenCalc  => DateTime.SpecifyKind(CreateDateSbe.Date, DateTimeKind.Utc);
    public DateTime CsDtCloseCalc => DateTime.SpecifyKind(CreateDateSbe.Date.AddHours(23).AddMinutes(59).AddSeconds(59), DateTimeKind.Utc);

    // -------------------------------------------------------------------------
    // Tipo
    // -------------------------------------------------------------------------
    public bool IsTicket   => IssuerType.Equals("TICKET",   StringComparison.OrdinalIgnoreCase);
    public bool IsRecharge => IssuerType.Equals("RECHARGE", StringComparison.OrdinalIgnoreCase);
    public bool IsCoupon   => IssuerType.Equals("COUPON",   StringComparison.OrdinalIgnoreCase);

    // -------------------------------------------------------------------------
    // Valores efetivos
    // -------------------------------------------------------------------------
    public DateTime ConfirmDate   => PaidDate ?? CreateDateSbe;
    public decimal  ReceivedValue => TotalValue;

    // -------------------------------------------------------------------------
    // cp_id: produto comercial
    // -------------------------------------------------------------------------
    public int CpIdCalc
    {
        get
        {
            if (IsTicket)   return 738;
            if (IsRecharge) return 723; // TODO: confirmar cp_id correto para recarga de cartão
            if (IsCoupon)   return 745;
            throw new InvalidOperationException($"IssuerType não suportado: '{IssuerType}'");
        }
    }

    // -------------------------------------------------------------------------
    // cpm_id: meio de pagamento
    // Confirmado via orders.payment.type: "PIX", "DEBIT", "CREDIT"
    // -------------------------------------------------------------------------
    public int? CpmIdCalc
    {
        get
        {
            if (PaymentType is null) return null;

            if (PaymentType.Equals("PIX",     StringComparison.OrdinalIgnoreCase)) return 5;
            if (PaymentType.Equals("CREDIT",  StringComparison.OrdinalIgnoreCase) ||
                PaymentType.Equals("CREDITO", StringComparison.OrdinalIgnoreCase) ||
                PaymentType.Equals("CRÉDITO", StringComparison.OrdinalIgnoreCase)) return 3;
            if (PaymentType.Equals("DEBIT",          StringComparison.OrdinalIgnoreCase) ||
                PaymentType.Equals("VIRTUAL_DEBIT",  StringComparison.OrdinalIgnoreCase) ||
                PaymentType.Equals("BALANCE",        StringComparison.OrdinalIgnoreCase) ||
                PaymentType.Equals("DEBITO",         StringComparison.OrdinalIgnoreCase) ||
                PaymentType.Equals("DÉBITO",         StringComparison.OrdinalIgnoreCase)) return 2;

            return null;
        }
    }

    // -------------------------------------------------------------------------
    // mediaType para com_trandt
    // -------------------------------------------------------------------------
    public int? MediaType => IsCoupon ? null : IsTicket ? 0 : 1;

    // -------------------------------------------------------------------------
    // Utilitário: converte ObjectId hex em long (primeiros 8 bytes = 64 bits)
    // -------------------------------------------------------------------------
    public static long ObjectIdToLong(string oid)
    {
        var hex = oid.Length >= 16 ? oid[..16] : oid.PadRight(16, '0');
        return Convert.ToInt64(hex, 16);
    }
}
