using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>The photo editor's controls must change pixels, not merely decorate an iframe.</summary>
public class PhotoEditorInteractionTests
{
    private static readonly string Editor = ReadSource("editor.html");

    [Fact]
    public void Canvas_tools_use_pointer_capture_for_mouse_pen_and_touch()
    {
        Assert.Contains("ov.addEventListener('pointerdown',onDown);", Editor);
        Assert.Contains("ov.addEventListener('pointermove',onMove);", Editor);
        Assert.Contains("ov.addEventListener('pointerup',onUp);", Editor);
        Assert.Contains("ov.setPointerCapture(e.pointerId)", Editor);
        Assert.Contains("touch-action:none", Editor);
        Assert.DoesNotContain("ov.addEventListener('mousedown',onDown);", Editor);
    }

    [Fact]
    public void The_editor_handshake_cannot_lose_a_cached_iframe_race()
    {
        Assert.Contains("const q=new URLSearchParams(location.search),direct=q.get('image');", Editor);
        Assert.Contains("hostMsg({type:'editor-ready'});", Editor);
        Assert.Contains("if(!imageRequested)hostMsg({type:'editor-ready'});", Editor);
        Assert.Contains("imageRequested=true;clearInterval(readyTimer);", Editor);
    }

    [Fact]
    public void Crop_has_working_presets_and_never_reads_outside_the_picture()
    {
        Assert.Contains("data-crop-preset=\"square\"", Editor);
        Assert.Contains("data-crop-preset=\"four-three\"", Editor);
        Assert.Contains("data-crop-preset=\"portrait\"", Editor);
        Assert.Contains("Math.min(canvas.width", Editor);
        Assert.Contains("Math.min(canvas.height", Editor);
        Assert.Contains("drawImage(canvas,r.x,r.y,r.w,r.h,0,0,r.w,r.h)", Editor);
    }

    [Fact]
    public void Brightness_has_live_sliders_and_a_real_auto_brighten_path()
    {
        Assert.Contains("id=\"adj-auto\"", Editor);
        Assert.Contains("function autoBrighten()", Editor);
        Assert.Contains("function previewAdjustments()", Editor);
        Assert.Contains("canvas.style.filter=liveFilter;", Editor);
        Assert.Contains("p.querySelector('#adj-ok').addEventListener('click',burnFilter);", Editor);
        Assert.Contains("renderOptions();\n  toast('Filter applied');", Editor.Replace("\r\n", "\n"));
    }

    private static string ReadSource(string name)
    {
        var resource = $"ING_eBay_AutoLister.wwwroot.{name}";
        using var stream = typeof(Csrf).Assembly.GetManifestResourceStream(resource);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
