using System.Runtime.InteropServices;

namespace CdBurner.Lithography.Imaging;

/// <summary>
/// Decodes validated PNG bytes via Windows Imaging Component (<c>windowscodecs.dll</c>) using raw
/// stdcall COM vtable calls, avoiding apartment-state conflicts on both STA and MTA threads.
/// </summary>
internal static unsafe class WicPngDecoder
{
    private const uint WicSdkVersion = 0x0236u;
    private const uint WicDecodeMetadataCacheOnDemand = 0u;

    private static readonly Guid GuidContainerFormatPng = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");
    private static readonly Guid GuidWicPixelFormat32bppBgra = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");

    [DllImport("windowscodecs.dll", ExactSpelling = true)]
    private static extern int WICCreateImagingFactory_Proxy(uint sdkVersion, out nint ppIImagingFactory);

    [DllImport("windowscodecs.dll", ExactSpelling = true)]
    private static extern int WICConvertBitmapSource(in Guid dstFormat, nint pISrc, out nint ppIDst);

    [DllImport("shlwapi.dll", ExactSpelling = true)]
    private static extern nint SHCreateMemStream(byte* pInit, uint cbInit);

    internal static BgraRaster Decode(ReadOnlySpan<byte> pngBytes)
    {
        if (pngBytes.IsEmpty)
        {
            throw new InvalidDataException("PNG data cannot be empty.");
        }

        nint factory = 0;
        nint memStream = 0;
        nint decoder = 0;
        nint frame = 0;
        nint converted = 0;

        try
        {
            int hr = WICCreateImagingFactory_Proxy(WicSdkVersion, out factory);
            if (hr < 0 || factory == 0)
            {
                throw new InvalidDataException($"Unable to initialize Windows Imaging Component (0x{hr:X8}).");
            }

            fixed (byte* pBytes = pngBytes)
            {
                memStream = SHCreateMemStream(pBytes, checked((uint)pngBytes.Length));
            }
            if (memStream == 0)
            {
                throw new InvalidDataException("Unable to allocate in-memory stream for PNG decoding.");
            }

            // IWICImagingFactory::CreateDecoderFromStream (vtable slot 4)
            nint* factoryVtable = *(nint**)factory;
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, uint, nint*, int>)factoryVtable[4])(
                factory, memStream, null, WicDecodeMetadataCacheOnDemand, &decoder);
            if (hr < 0 || decoder == 0)
            {
                throw new InvalidDataException($"Failed to decode PNG stream (0x{hr:X8}).");
            }

            // IWICBitmapDecoder::GetContainerFormat (vtable slot 5)
            nint* decoderVtable = *(nint**)decoder;
            Guid containerFormat = default;
            hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, int>)decoderVtable[5])(decoder, &containerFormat);
            if (hr < 0 || containerFormat != GuidContainerFormatPng)
            {
                throw new InvalidDataException("Decoded image container is not PNG.");
            }

            // IWICBitmapDecoder::GetFrame (vtable slot 13)
            hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)decoderVtable[13])(decoder, 0u, &frame);
            if (hr < 0 || frame == 0)
            {
                throw new InvalidDataException($"Failed to read PNG frame 0 (0x{hr:X8}).");
            }

            // Convert strictly to straight (unpremultiplied) 32bppBGRA.
            hr = WICConvertBitmapSource(in GuidWicPixelFormat32bppBgra, frame, out converted);
            if (hr < 0 || converted == 0)
            {
                throw new InvalidDataException($"Failed to convert PNG frame to 32bppBGRA (0x{hr:X8}).");
            }

            // IWICBitmapSource::GetSize (vtable slot 3)
            nint* convertedVtable = *(nint**)converted;
            uint width = 0;
            uint height = 0;
            hr = ((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)convertedVtable[3])(converted, &width, &height);
            if (hr < 0)
            {
                throw new InvalidDataException($"Failed to query PNG dimensions (0x{hr:X8}).");
            }

            if (width == 0 || height == 0 || (long)width * height > BgraRaster.MaxPixels)
            {
                throw new InvalidDataException("PNG picture size is out of range.");
            }

            uint stride = checked(width * 4u);
            uint bufferSize = checked(stride * height);
            byte[] pixels = new byte[bufferSize];

            // IWICBitmapSource::CopyPixels (vtable slot 7)
            fixed (byte* pPixels = pixels)
            {
                hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, byte*, int>)convertedVtable[7])(
                    converted, 0, stride, bufferSize, pPixels);
            }
            if (hr < 0)
            {
                throw new InvalidDataException($"Failed to copy PNG pixels (0x{hr:X8}).");
            }

            return new BgraRaster(checked((int)width), checked((int)height), pixels);
        }
        finally
        {
            ReleaseComObject(converted);
            ReleaseComObject(frame);
            ReleaseComObject(decoder);
            ReleaseComObject(memStream);
            ReleaseComObject(factory);
        }
    }

    private static void ReleaseComObject(nint ptr)
    {
        if (ptr != 0)
        {
            nint* vtable = *(nint**)ptr;
            ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[2])(ptr);
        }
    }
}
