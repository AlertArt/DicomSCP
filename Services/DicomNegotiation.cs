using FellowOakDicom;
using FellowOakDicom.Network;

namespace DicomSCP.Services;

/// <summary>
/// DICOM PS3.8 传输语法协商与表示上下文（Presentation Context）分配的统一入口。
/// PC ID 规则（PS3.8 9.3.2）：必须为 1–255 范围内的奇数。
/// </summary>
public static class DicomNegotiation
{
    /// <summary>
    /// 校验/查询类服务的接受语法（SCP 侧偏好顺序：显式优先于隐式）。
    /// </summary>
    public static readonly DicomTransferSyntax[] SupportedBasicSyntaxes =
    [
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian
    ];

    /// <summary>
    /// 图像存储类的接受语法（原 CStoreSCP 与 QRSCP 两处硬编码列表的并集）。
    /// 压缩语法在前为 SCP 侧偏好，末尾三个基础语法作兜底。
    /// </summary>
    public static readonly DicomTransferSyntax[] SupportedImageStorageSyntaxes =
    [
        DicomTransferSyntax.JPEGLSLossless,
        DicomTransferSyntax.JPEG2000Lossless,
        DicomTransferSyntax.RLELossless,
        DicomTransferSyntax.JPEGLSNearLossless,
        DicomTransferSyntax.JPEG2000Lossy,
        DicomTransferSyntax.JPEGProcess14SV1,
        DicomTransferSyntax.JPEGProcess1,
        DicomTransferSyntax.JPEGProcess2_4,
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian
    ];

    public const byte MinPresentationContextId = 1;
    public const byte MaxPresentationContextId = 255;

    /// <summary>
    /// 从对端提议的语法列表中选出双方均支持的第一个（保持对端偏好序）。
    /// 无交集时返回 null，由调用方决定拒绝原因。
    /// </summary>
    public static DicomTransferSyntax? SelectBestMatch(
        IEnumerable<DicomTransferSyntax> offered,
        IEnumerable<DicomTransferSyntax> supported)
    {
        var supportedIds = new HashSet<string>(
            supported.Select(s => s.UID.UID), StringComparer.Ordinal);

        return offered.FirstOrDefault(o => supportedIds.Contains(o.UID.UID));
    }

    /// <summary>
    /// 计算需要新增的表示上下文（纯函数，不修改入参）：
    /// - 已覆盖同一抽象语法的上下文不重复创建（C-MOVE 跨批次幂等）；
    /// - PC ID 分配尚未占用的最小奇数，ID 空间耗尽时停止分配而非回绕溢出。
    /// </summary>
    public static IReadOnlyList<DicomPresentationContext> ComputeMissingPresentationContexts(
        IEnumerable<DicomPresentationContext> existing,
        IEnumerable<DicomUID> sopClassUids,
        DicomTransferSyntax[] acceptedSyntaxes)
    {
        var covered = new HashSet<string>(
            existing.Select(pc => pc.AbstractSyntax?.UID ?? string.Empty),
            StringComparer.Ordinal);

        var usedIds = new HashSet<byte>(existing.Select(pc => pc.ID));
        var result = new List<DicomPresentationContext>();

        foreach (var sopClass in sopClassUids)
        {
            if (sopClass == null || !covered.Add(sopClass.UID))
            {
                continue;
            }

            var id = NextFreeOddId(usedIds);
            if (id == null)
            {
                break;
            }

            var pc = new DicomPresentationContext(id.Value, sopClass);
            pc.AcceptTransferSyntaxes(acceptedSyntaxes);
            result.Add(pc);
            usedIds.Add(id.Value);
        }

        return result;
    }

    /// <summary>分配未占用的最小奇数 PC ID；1–255 全部占用时返回 null（不回绕）。</summary>
    private static byte? NextFreeOddId(ISet<byte> used)
    {
        for (var id = (int)MinPresentationContextId; id <= (int)MaxPresentationContextId; id += 2)
        {
            if (!used.Contains((byte)id))
            {
                return (byte)id;
            }
        }

        return null;
    }
}
