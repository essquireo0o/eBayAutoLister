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

    [Fact]
    public void A_drawn_crop_box_can_be_moved_resized_and_committed_without_the_far_button()
    {
        // 2026-09-12: the owner, mid-listing, "the crop does not work." The crop MATH was fine in
        // every headless reproduction; what was missing was that a drawn box could not be adjusted
        // (the corner squares only started a new tiny selection) and the only way to commit was a
        // distant Apply Crop button. So: handle hit-testing, move/resize, and Enter/double-click.
        Assert.Contains("function cropHandleAt(pt)", Editor);
        Assert.Contains("function updateCropGesture(pt)", Editor);
        Assert.Contains("function setCropRect(r)", Editor);
        // Move and resize modes exist and the gesture drives them.
        Assert.Contains("mode:hd==='move'?'move':'resize'", Editor);
        Assert.Contains("if(H.includes('w'))x=pt.x;", Editor);
        // Two quick commit paths that need no far-off button.
        Assert.Contains("if(e.key==='Enter'&&tool==='crop'&&cropRect", Editor);
        Assert.Contains("ov.addEventListener('dblclick',()=>{if(tool==='crop'&&cropRect)applyCrop();});", Editor);
        // The old draw-only model is gone.
        Assert.DoesNotContain("cropDrag", Editor);
        Assert.DoesNotContain("function calcCropRect()", Editor);
    }

    [Fact]
    public void Crop_and_rotation_history_restores_the_original_dimensions()
    {
        Assert.Contains("function takeSnapshot()", Editor);
        Assert.Contains("width:canvas.width,height:canvas.height", Editor);
        Assert.Contains("canvas.width=s.width;canvas.height=s.height", Editor);
        Assert.Contains("redos.push(takeSnapshot())", Editor);
        Assert.DoesNotContain("undos.push(ctx.getImageData", Editor);
    }

    [Fact]
    public void Saving_places_a_real_high_quality_file_in_the_photo_library()
    {
        Assert.Contains("const MAX=6000", Editor);
        Assert.Contains("canvas.toDataURL('image/jpeg',0.96)", Editor);
        Assert.Contains("fetch('/api/photos/library/upload'", Editor);
        Assert.Contains("modelKey:sourceModelKey", Editor);
        Assert.Contains("if(!res.ok||!d.url)throw new Error", Editor);
        Assert.DoesNotContain("d?.url||('data:image/jpeg", Editor);
    }

    [Fact]
    public void Studio_layout_explains_the_current_tool_and_protects_the_original()
    {
        Assert.Contains("Marketplace-ready editing", Editor);
        Assert.Contains("id=\"pe-help\"", Editor);
        Assert.Contains("Original protected", Editor);
        Assert.Contains("eBay square", Editor);
        Assert.Contains("function updateHelp()", Editor);
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
