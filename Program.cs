using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .Build();

var mongoConn       = config["Mongo:ConnectionString"]    ?? throw new Exception("Mongo:ConnectionString ausente.");
var checkoutDb      = config["Mongo:CheckoutDatabase"]   ?? throw new Exception("Mongo:CheckoutDatabase ausente.");
var mobilityDb      = config["Mongo:MobilityDatabase"]   ?? throw new Exception("Mongo:MobilityDatabase ausente.");
var mongoServerSelectionTimeout = int.TryParse(config["Mongo:ServerSelectionTimeoutSeconds"], out var sst) ? sst : 30;
var pgConn          = config["Postgres:ConnectionString"] ?? throw new Exception("Postgres:ConnectionString ausente.");
var blameUser       = config["Import:User"]  ?? "IMPORT APP MONGO";
var cttId           = int.TryParse(config["Import:CttId"], out var ctt) ? ctt : 2;

// Args:
//   MongoImporter "YYYY-MM-DD HH:MM:SS" "YYYY-MM-DD HH:MM:SS" [force]
//
//   force  — ignora anti-join e reimporta registros já existentes no Postgres
if (args.Length < 2)
{
    Console.WriteLine("Uso: MongoImporter \"YYYY-MM-DD HH:MM:SS\" \"YYYY-MM-DD HH:MM:SS\" [force]");
    return;
}

var startTs     = BrazilTimeZone.ToUtc(DateTime.Parse(args[0]));
var endTs       = BrazilTimeZone.ToUtc(DateTime.Parse(args[1]));
var forceImport = args.Any(a => a.Equals("force", StringComparison.OrdinalIgnoreCase));

var importer = new Importer(
    mongoConnStr: mongoConn,
    checkoutDatabase: checkoutDb,
    mobilityDatabase: mobilityDb,
    pgConnStr: pgConn,
    blameUser: blameUser,
    cttId: cttId,
    mongoServerSelectionTimeoutSeconds: mongoServerSelectionTimeout
);

await importer.RunAsync(startTs, endTs, forceImport);
