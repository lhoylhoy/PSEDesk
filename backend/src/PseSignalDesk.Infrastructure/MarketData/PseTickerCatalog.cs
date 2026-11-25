namespace PseSignalDesk.Infrastructure.MarketData;

/// <summary>
/// Metadata for one tracked PSE name. It carries no prices, volatility or turnover
/// assumptions: every candle, quote and turnover figure must come from the live
/// Yahoo Finance chart feed for this ticker.
/// <para>
/// <paramref name="SearchAliases"/> and <paramref name="DisclosurePatterns"/> exist because PSE
/// Edge headlines name the company, not the ticker. Matching a headline on a generic word taken
/// from the legal name ("Philippine", "Manila", "Banking") attributes one company's filing to
/// another; these explicit phrases keep attribution exact. Both are matched on word boundaries.
/// </para>
/// </summary>
public sealed record PseTickerInfo(
    string Ticker,
    string YahooSymbol,
    string CompanyName,
    string Sector,
    IReadOnlyList<string>? SearchAliases = null,
    IReadOnlyList<string>? DisclosurePatterns = null);

public static class PseTickerCatalog
{
    /// <summary>
    /// Liquid and research-worthy PSE names tracked by the desk. Tickers use Yahoo Finance's
    /// PSE suffix (<c>{TICKER}.PS</c>) and must be the exchange's own symbols: a wrong symbol
    /// 404s on Yahoo and never matches an official Daily Quotation Report row. The list
    /// deliberately mixes large caps, mid caps, dividend compounders and REITs: the PHP 5M
    /// turnover floor — not the catalog — decides eligibility, so a thin REIT simply drops out
    /// of a run instead of being excluded here.
    /// </summary>
    public static IReadOnlyList<PseTickerInfo> All { get; } = new List<PseTickerInfo>
    {
        new("ICT", "ICT.PS", "International Container Terminal Services, Inc.", "Industrials",
            new[] { "ICTSI" }),
        new("BDO", "BDO.PS", "BDO Unibank, Inc.", "Financials",
            new[] { "BDO Unibank", "Banco de Oro" }),
        new("SM", "SM.PS", "SM Investments Corporation", "Holding Firms",
            new[] { "SM Investments" }),
        new("ALI", "ALI.PS", "Ayala Land, Inc.", "Property",
            new[] { "Ayala Land" }),
        new("BPI", "BPI.PS", "Bank of the Philippine Islands", "Financials",
            new[] { "Bank of the Philippine Islands", "BPI" }),
        new("JFC", "JFC.PS", "Jollibee Foods Corporation", "Consumer",
            new[] { "Jollibee" }),
        new("MER", "MER.PS", "Manila Electric Company", "Utilities",
            new[] { "Manila Electric", "Meralco" }),
        new("TEL", "TEL.PS", "PLDT Inc.", "Telecommunications",
            new[] { "PLDT", "Philippine Long Distance Telephone" }),
        new("GLO", "GLO.PS", "Globe Telecom, Inc.", "Telecommunications",
            new[] { "Globe Telecom" }),
        new("AC", "AC.PS", "Ayala Corporation", "Holding Firms",
            new[] { "Ayala Corporation" }),
        new("SMPH", "SMPH.PS", "SM Prime Holdings, Inc.", "Property",
            new[] { "SM Prime" }),
        new("MBT", "MBT.PS", "Metropolitan Bank & Trust Company", "Financials",
            new[] { "Metropolitan Bank", "Metrobank" }),
        new("URC", "URC.PS", "Universal Robina Corporation", "Consumer",
            new[] { "Universal Robina" }),
        new("CNVRG", "CNVRG.PS", "Converge ICT Solutions, Inc.", "Telecommunications",
            new[] { "Converge ICT" }),
        new("MONDE", "MONDE.PS", "Monde Nissin Corporation", "Consumer",
            new[] { "Monde Nissin" }),
        new("DMC", "DMC.PS", "DMCI Holdings, Inc.", "Industrials",
            new[] { "DMCI" }),
        new("SCC", "SCC.PS", "Semirara Mining and Power Corporation", "Mining",
            new[] { "Semirara" }),
        new("AP", "AP.PS", "Aboitiz Power Corporation", "Utilities",
            new[] { "Aboitiz Power" }),
        new("AEV", "AEV.PS", "Aboitiz Equity Ventures, Inc.", "Holding Firms",
            new[] { "Aboitiz Equity" }),
        new("PGOLD", "PGOLD.PS", "Puregold Price Club, Inc.", "Consumer",
            new[] { "Puregold" }),
        new("GTCAP", "GTCAP.PS", "GT Capital Holdings, Inc.", "Holding Firms",
            new[] { "GT Capital" }),
        new("AGI", "AGI.PS", "Alliance Global Group, Inc.", "Holding Firms",
            new[] { "Alliance Global" }),
        new("LTG", "LTG.PS", "LT Group, Inc.", "Holding Firms",
            new[] { "LT Group" }),
        new("BLOOM", "BLOOM.PS", "Bloomberry Resorts Corporation", "Consumer",
            new[] { "Bloomberry" }),
        new("RLC", "RLC.PS", "Robinsons Land Corporation", "Property",
            new[] { "Robinsons Land" }),
        new("MEG", "MEG.PS", "Megaworld Corporation", "Property",
            new[] { "Megaworld" }),
        new("AREIT", "AREIT.PS", "AREIT, Inc.", "REIT",
            new[] { "AREIT" }),
        new("MREIT", "MREIT.PS", "MREIT, Inc.", "REIT",
            new[] { "MREIT" }),
        new("CREIT", "CREIT.PS", "Citicore Energy REIT Corporation", "REIT",
            new[] { "Citicore Energy REIT" }),
        new("PLUS", "PLUS.PS", "DigiPlus Interactive Corp.", "Consumer",
            new[] { "DigiPlus" }),
        new("ACEN", "ACEN.PS", "ACEN Corporation", "Utilities",
            new[] { "ACEN" }),
        new("CBC", "CBC.PS", "China Banking Corporation", "Financials",
            new[] { "China Bank", "China Banking" }),
        new("CNPF", "CNPF.PS", "Century Pacific Food, Inc.", "Consumer",
            new[] { "Century Pacific" }),
        new("JGS", "JGS.PS", "JG Summit Holdings, Inc.", "Holding Firms",
            new[] { "JG Summit" }),

        // Dividend compounders and conglomerates that carry the income tilt of the
        // MONTH/YEAR books.
        new("SMC", "SMC.PS", "San Miguel Corporation", "Holding Firms",
            new[] { "San Miguel Corporation" }),
        // The exchange symbol is FB, not SMFB: Yahoo's SMFB.PS 404s and the official Daily
        // Quotation Report prints FB, so the wrong symbol silently removed the name.
        new("FB", "FB.PS", "San Miguel Food and Beverage, Inc.", "Consumer",
            new[] { "San Miguel Food and Beverage", "San Miguel Food" }),
        new("GSMI", "GSMI.PS", "Ginebra San Miguel, Inc.", "Consumer",
            new[] { "Ginebra San Miguel", "Ginebra" }),
        new("PCOR", "PCOR.PS", "Petron Corporation", "Industrials",
            new[] { "Petron" }),
        new("COSCO", "COSCO.PS", "Cosco Capital, Inc.", "Holding Firms",
            new[] { "Cosco Capital" }),
        // The exchange symbol is EMI, not EM.
        new("EMI", "EMI.PS", "Emperador Inc.", "Consumer",
            new[] { "Emperador" }),
        new("SSI", "SSI.PS", "SSI Group, Inc.", "Consumer",
            new[] { "SSI Group" }),

        // Mid-cap industrials and infrastructure.
        new("MWIDE", "MWIDE.PS", "Megawide Construction Corporation", "Industrials",
            new[] { "Megawide" }),
        new("IMI", "IMI.PS", "Integrated Micro-Electronics, Inc.", "Industrials",
            new[] { "Integrated Micro-Electronics" }),
        new("CEB", "CEB.PS", "Cebu Air, Inc.", "Industrials",
            new[] { "Cebu Air", "Cebu Pacific" }),

        // Banks outside the index heavyweights.
        new("SECB", "SECB.PS", "Security Bank Corporation", "Financials",
            new[] { "Security Bank" }),
        new("UBP", "UBP.PS", "Union Bank of the Philippines", "Financials",
            new[] { "Union Bank", "UnionBank" }),
        // The exchange symbol is RCB, not RCBC.
        new("RCB", "RCB.PS", "Rizal Commercial Banking Corporation", "Financials",
            new[] { "Rizal Commercial Banking", "RCBC" }),
        new("PNB", "PNB.PS", "Philippine National Bank", "Financials",
            new[] { "Philippine National Bank" }),
        new("EW", "EW.PS", "East West Banking Corporation", "Financials",
            new[] { "East West Banking", "EastWest Bank" }),

        // Power generation and grids.
        new("FGEN", "FGEN.PS", "First Gen Corporation", "Utilities",
            new[] { "First Gen" }),
        new("SGP", "SGP.PS", "Synergy Grid & Development Phils., Inc.", "Utilities",
            new[] { "Synergy Grid" }),

        // Miners.
        new("NIKL", "NIKL.PS", "Nickel Asia Corporation", "Mining",
            new[] { "Nickel Asia" }),
        new("PX", "PX.PS", "Philex Mining Corporation", "Mining",
            new[] { "Philex Mining" }),

        // Property, retail and logistics.
        new("FLI", "FLI.PS", "Filinvest Land, Inc.", "Property",
            new[] { "Filinvest Land" }),
        new("VLL", "VLL.PS", "Vista Land & Lifescapes, Inc.", "Property",
            new[] { "Vista Land" }),
        new("ALLHC", "ALLHC.PS", "AyalaLand Logistics Holdings Corp.", "Property",
            new[] { "AyalaLand Logistics" }),
        new("WLCON", "WLCON.PS", "Wilcon Depot, Inc.", "Consumer",
            new[] { "Wilcon Depot", "Wilcon" }),
        new("RRHI", "RRHI.PS", "Robinsons Retail Holdings, Inc.", "Consumer",
            new[] { "Robinsons Retail" }),

        // Telecom challenger. "DITO" is only ever matched as a whole word: as a substring it
        // appears inside "auditor" and "creditor", which would attribute audit disclosures to
        // the telco.
        new("DITO", "DITO.PS", "DITO CME Holdings Corp.", "Telecommunications",
            new[] { "DITO CME" }),

        // Listed REITs: the core cash-yield sleeve for the long books.
        new("FILRT", "FILRT.PS", "Filinvest REIT Corp.", "REIT",
            new[] { "Filinvest REIT" }),
        new("DDMPR", "DDMPR.PS", "DDMP REIT, Inc.", "REIT",
            new[] { "DDMP REIT" }),
        new("VREIT", "VREIT.PS", "VistaREIT, Inc.", "REIT",
            new[] { "VistaREIT" }),
        new("PREIT", "PREIT.PS", "Premiere Island Power REIT Corporation", "REIT",
            new[] { "Premiere Island Power REIT" }),
        new("RCR", "RCR.PS", "RL Commercial REIT, Inc.", "REIT",
            new[] { "RL Commercial REIT" })
    };

    public static PseTickerInfo? Find(string ticker) =>
        All.FirstOrDefault(t => string.Equals(t.Ticker, ticker, StringComparison.OrdinalIgnoreCase));
}
