using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Moye.Services;

/// <summary>Detaches annotation behavior from the source document's page order,
/// without removing annotations, their appearance streams or page content.</summary>
internal static class PdfAnnotationActions
{
    public static bool MakeStatic(PdfDictionary annotation)
    {
        var changed = annotation.Elements.Remove("/Dest");
        changed |= annotation.Elements.Remove("/AA");
        var action = Resolve(annotation.Elements["/A"]);
        if (action is PdfDictionary dictionary && Resolve(dictionary.Elements["/S"]) is
            (PdfName { Value: "/URI" } or PdfNameObject { Value: "/URI" }))
        {
            // Keep ordinary external links, but not an action chain that may
            // jump to a deleted/reordered page or execute another interaction.
            changed |= dictionary.Elements.Remove("/Next");
        }
        else changed |= annotation.Elements.Remove("/A");
        return changed;
    }

    private static PdfItem? Resolve(PdfItem? value) => value is PdfReference reference ? reference.Value : value;
}
