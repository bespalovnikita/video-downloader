using System.Windows.Media.Imaging;

namespace VideoDownloader.Native;

internal static class PreviewImage
{
    // A file signature is insufficient: truncated images can still carry a
    // correct PNG/JPEG header. Decode a bounded raster before accepting it.
    internal static BitmapImage Decode(byte[] bytes)
    {
        if(bytes.Length==0 || bytes.Length>2_000_000)throw new InvalidDataException("Invalid thumbnail size.");
        int width,height;
        using(var metadata=new MemoryStream(bytes,false))
        {
            var decoder=BitmapDecoder.Create(metadata,BitmapCreateOptions.IgnoreColorProfile,BitmapCacheOption.None);
            if(decoder.Frames.Count==0)throw new InvalidDataException("Thumbnail has no image frame.");
            width=decoder.Frames[0].PixelWidth;height=decoder.Frames[0].PixelHeight;
            if(width<=0 || height<=0 || (long)width*height>16_777_216)
                throw new InvalidDataException("Thumbnail dimensions exceed the preview limit.");
        }
        using var input=new MemoryStream(bytes,false);
        double scale=Math.Min(1,720d/Math.Max(width,height));
        var image=new BitmapImage();
        image.BeginInit();
        image.CacheOption=BitmapCacheOption.OnLoad;
        image.CreateOptions=BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth=Math.Max(1,(int)Math.Round(width*scale));
        image.DecodePixelHeight=Math.Max(1,(int)Math.Round(height*scale));
        image.StreamSource=input;
        image.EndInit();
        // Force lazy decoders to consume the complete displayed frame too.
        int stride=checked((image.PixelWidth*image.Format.BitsPerPixel+7)/8);
        image.CopyPixels(new byte[checked(stride*image.PixelHeight)],stride,0);
        image.Freeze();
        return image;
    }

    internal static bool IsValid(byte[] bytes)
    {
        try { Decode(bytes);return true; }
        catch(Exception) { return false; }
    }
}
