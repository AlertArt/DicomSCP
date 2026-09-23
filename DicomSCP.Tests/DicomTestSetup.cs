using System.Runtime.CompilerServices;
using FellowOakDicom;
using FellowOakDicom.Imaging.NativeCodec;
using Microsoft.Extensions.DependencyInjection;

namespace DicomSCP.Tests;

/// <summary>
/// 测试进程 fo-dicom 初始化：注册原生编解码器（JPEG/JPEG-LS/JPEG2000/RLE），
/// 使转码相关单元测试与服务端行为一致。
/// </summary>
internal static class DicomTestSetup
{
    [ModuleInitializer]
    public static void Initialize()
    {
        new DicomSetupBuilder()
            .RegisterServices(services => services
                .AddFellowOakDicom()
                .AddTranscoderManager<NativeTranscoderManager>())
            .Build();
    }
}
