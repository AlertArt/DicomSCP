using System.ComponentModel.DataAnnotations;

namespace DicomSCP.Configuration;

public class PrinterConfig
{
    public string Name { get; set; } = "";
    public string AeTitle { get; set; } = "";
    public string HostName { get; set; } = "";
    public int Port { get; set; } = 104;
    public bool IsDefault { get; set; }
    public string Description { get; set; } = "";
}

public class PrintScuConfig
{
    public string AeTitle { get; set; } = "";
    public List<PrinterConfig> Printers { get; set; } = new();
}

public class DicomSettings
{
    [Required]
    [RegularExpression(@"^[A-Za-z0-9\-_]{1,16}$")]
    public string AeTitle { get; set; } = "STORESCP";

    [Range(1, 65535)]
    public int StoreSCPPort { get; set; } = 11112;

    [Required]
    public string StoragePath { get; set; } = "./received_files";

    [Required]
    public string TempPath { get; set; } = "./temp_files";

    public AdvancedSettings Advanced { get; set; } = new();

    public WorklistSCPSettings WorklistSCP { get; set; } = new();

    public QRSCPSettings QRSCP { get; set; } = new();

    public PrintSCPSettings PrintSCP { get; set; } = new();

    public StorageCommitmentSCPSettings StorageCommitmentSCP { get; set; } = new();

    public MppsSCPSettings MppsSCP { get; set; } = new();

    public UpsSCPSettings UpsSCP { get; set; } = new();

    public PrintScuConfig? PrintSCU { get; set; }
    
    public List<PrinterConfig> Printers { get; set; } = new();
}

public class WorklistSCPSettings
{
    [Required]
    [RegularExpression(@"^[A-Za-z0-9\-_]{1,16}$")]
    public string AeTitle { get; set; } = "WORKLISTSCP";

    [Range(1, 65535)]
    public int Port { get; set; } = 11113;

    public bool ValidateCallingAE { get; set; } = false;
    public string[] AllowedCallingAEs { get; set; } = Array.Empty<string>();
}

public class AdvancedSettings
{
    public bool ValidateCallingAE { get; set; } = false;
    public string[] AllowedCallingAEs { get; set; } = Array.Empty<string>();

    public bool EnableCompression { get; set; } = false;
    public string PreferredTransferSyntax { get; set; } = "JPEG2000Lossless";
}

public class QRSCPSettings
{
    public string AeTitle { get; set; } = "QRSCP";
    public int Port { get; set; } = 11114;
    public bool ValidateCallingAE { get; set; }
    public List<string> AllowedCallingAEs { get; set; } = new();
    public List<MoveDestination> MoveDestinations { get; set; } = new();
}

public class MoveDestination
{
    public string Name { get; set; } = string.Empty;
    public string AeTitle { get; set; } = string.Empty;
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 11112;
}

public class PrintSCPSettings
{
    [Required]
    [RegularExpression(@"^[A-Za-z0-9\-_]{1,16}$")]
    public string AeTitle { get; set; } = "PRINTSCP";

    [Range(1, 65535)]
    public int Port { get; set; } = 11115;

    public bool ValidateCallingAE { get; set; } = false;
    public string[] AllowedCallingAEs { get; set; } = Array.Empty<string>();
}

public class StorageCommitmentSCPSettings
{
    [Required]
    [RegularExpression(@"^[A-Za-z0-9\-_]{1,16}$")]
    public string AeTitle { get; set; } = "STORECOMMITSCP";

    [Range(1, 65535)]
    public int Port { get; set; } = 11116;

    public bool ValidateCallingAE { get; set; } = false;
    public string[] AllowedCallingAEs { get; set; } = Array.Empty<string>();

    /// <summary>推送 N-EVENT-REPORT 时使用的重连尝试次数（默认 3 次）。</summary>
    public int PushRetryCount { get; set; } = 3;

    /// <summary>存储承诺记录保留天数（TTL，默认 30 天）；0 表示不过期。</summary>
    [Range(0, 3650)]
    public int RecordTtlDays { get; set; } = 30;
}

public class MppsSCPSettings
{
    [Required]
    [RegularExpression(@"^[A-Za-z0-9\-_]{1,16}$")]
    public string AeTitle { get; set; } = "MPPSSCP";

    [Range(1, 65535)]
    public int Port { get; set; } = 11117;

    public bool ValidateCallingAE { get; set; } = false;
    public string[] AllowedCallingAEs { get; set; } = Array.Empty<string>();
}

/// <summary>
/// 统一程序步骤（UPS）服务设置。GPWL（通用用途工作清单，RETIRED）已被 UPS 取代，不再单独提供。
/// </summary>
public class UpsSCPSettings
{
    [Required]
    [RegularExpression(@"^[A-Za-z0-9\-_]{1,16}$")]
    public string AeTitle { get; set; } = "UPSSCP";

    [Range(1, 65535)]
    public int Port { get; set; } = 11118;

    public bool ValidateCallingAE { get; set; } = false;
    public string[] AllowedCallingAEs { get; set; } = Array.Empty<string>();

    /// <summary>推送 N-EVENT-REPORT 时使用的重连尝试次数（默认 3 次）。</summary>
    public int PushRetryCount { get; set; } = 3;
}