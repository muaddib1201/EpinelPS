using Microsoft.AspNetCore.Mvc;

namespace EpinelPS.Controllers;

// Fake payment page for the PC client's Jupiter purchase flow, the order is already paid by the time it opens
[Route("jupiter")]
public class JupiterPayController : Controller
{
    [HttpGet]
    [Route("pay/{referenceId}")]
    public ContentResult Pay(string referenceId)
    {
        // The client only queries the order once this window closes, so close it right away.
        // Dark background so the brief flash doesn't show as a white frame.
        const string html = """
            <!doctype html>
            <html style="background:#111"><head><meta charset="utf-8"><title>Payment complete</title>
            <script>window.close();</script></head>
            <body style="background:#111;color:#888;font-family:sans-serif;text-align:center;padding-top:40px">
            <p>付款完成 / Payment complete</p>
            </body></html>
            """;

        return Content(html, "text/html; charset=utf-8");
    }
}
