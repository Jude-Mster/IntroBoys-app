using Android.Gms.Tasks;
using Android.Graphics;
using Xamarin.Google.MLKit.Vision.Common;
using Xamarin.Google.MLKit.Vision.Text;
using Xamarin.Google.MLKit.Vision.Text.Latin;

namespace IntroBoys.App;

/// <summary>One line of text read from the screen, with where it is (screen pixels).</summary>
public readonly record struct TextLine(string Text, float X, float Y, float W, float H)
{
    public float Cx => X + W / 2;
    public float Cy => Y + H / 2;
}

/// <summary>
/// Reads text from pictures of the screen with Google's on-phone text recognition (built into the app; nothing is sent anywhere).
/// Used for the position under the mini-map, the map name, the teleport list, and the name of the target.
/// </summary>
static class Ocr
{
    static ITextRecognizer? client;
    static readonly SemaphoreSlim one = new(1, 1);

    /// <summary>
    /// The lines of text in a part of the frame. The part is made <paramref name="scale"/> times bigger first (small text reads better).
    /// Positions are given back in screen pixels.
    /// </summary>
    public static async Task<List<TextLine>> ReadAsync(Frame f, int x, int y, int w, int h, int scale = 1)
    {
        var lines = new List<TextLine>();
        await one.WaitAsync();
        Bitmap? bmp = null;
        try
        {
            client ??= TextRecognition.GetClient(TextRecognizerOptions.DefaultOptions);
            bmp = f.Crop(x, y, w, h, scale);
            var img = InputImage.FromBitmap(bmp, 0);
            var done = new TaskCompletionSource<Java.Lang.Object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = client.Process(img);
            task.AddOnSuccessListener(new Ok(done));
            task.AddOnFailureListener(new Bad(done));
            var timeout = System.Threading.Tasks.Task.Delay(4000);
            var first = await System.Threading.Tasks.Task.WhenAny(done.Task, timeout);
            if (first != done.Task) return lines;
            if (await done.Task is not Text text) return lines;
            foreach (var block in text.TextBlocks)
                foreach (var line in block.Lines)
                {
                    var r = line.BoundingBox;
                    if (r == null) continue;
                    lines.Add(new TextLine(line.Text ?? "", x + r.Left / (float)scale, y + r.Top / (float)scale, r.Width() / (float)scale, r.Height() / (float)scale));
                }
        }
        catch (Exception ex) { AppLog.Add("read: " + ex.Message); }
        finally
        {
            bmp?.Recycle();
            one.Release();
        }
        return lines;
    }

    sealed class Ok : Java.Lang.Object, IOnSuccessListener
    {
        readonly TaskCompletionSource<Java.Lang.Object?> t;
        public Ok(TaskCompletionSource<Java.Lang.Object?> t) { this.t = t; }
        public void OnSuccess(Java.Lang.Object? result) => t.TrySetResult(result);
    }

    sealed class Bad : Java.Lang.Object, IOnFailureListener
    {
        readonly TaskCompletionSource<Java.Lang.Object?> t;
        public Bad(TaskCompletionSource<Java.Lang.Object?> t) { this.t = t; }
        public void OnFailure(Java.Lang.Exception e) => t.TrySetResult(null);
    }
}
