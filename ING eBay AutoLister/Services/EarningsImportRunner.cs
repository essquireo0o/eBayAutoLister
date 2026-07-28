using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Pulls sold orders from eBay into the earnings tracker. Lifted out of the
/// <c>/api/earnings/import</c> endpoint unchanged so that the button and the automatic refresh
/// (<see cref="EarningsAutoImport"/>) run the same code — a scheduled import that behaved even
/// slightly differently from the manual one would be a second implementation to keep in step, and
/// the first thing to silently drift.
///
/// The rule this must never break: <b>eBay owns what eBay knows, the seller owns their costs.</b>
/// Price, fee, refunds and title are re-read from eBay every time; the cost basis, shipping and
/// notes the seller typed are carried forward. Getting that backwards would make a re-import — and
/// therefore the automatic one — a way to silently destroy their work.
/// </summary>
public class EarningsImportRunner(
    EbayService ebay, EarningsStore store, CostBasisStore costBasis, ActionLog log)
{
    /// <summary>Import the last <paramref name="days"/> days of sold orders. Never throws for an ordinary failure — the status says what happened.</summary>
    public async Task<EarningsImportResult> RunAsync(int days, CancellationToken ct = default)
    {
        var window = Math.Clamp(days, 1, 730);
        var import = new EarningsImportResult { DaysRequested = window };

        List<EbayOrderSummary> orders;
        try
        {
            orders = await ebay.GetOrdersAsync(window, maxOrders: 1000, ct);
        }
        catch (EbayPermissionException ex)
        {
            import.Status = "reconnect";
            import.Message = ex.Message;
            return import;
        }
        catch (InvalidOperationException ex)
        {
            // No token at all — "connect eBay first", not an error.
            import.Status = "not_connected";
            import.Message = ex.Message;
            return import;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Add("Warning", "Earnings import failed", ex.Message);
            import.Status = "error";
            import.Message = ex.Message;
            return import;
        }

        import.OrdersRead = orders.Count;
        var knownCosts = costBasis.GetAll();
        // Read once, not once per line: a 1,000-order import would otherwise re-read the whole
        // flips table for every line item in it.
        var existingByKey = store.GetAll()
            .Where(f => !string.IsNullOrWhiteSpace(f.OrderId) && !string.IsNullOrWhiteSpace(f.LineItemId))
            .ToDictionary(f => (f.OrderId, f.LineItemId));

        foreach (var order in orders)
        {
            if (!EarningsImporter.IsCountable(order))
            {
                import.LinesSkipped += Math.Max(1, order.LineItems.Count);
                continue;
            }

            if (order.TotalMarketplaceFee.HasValue) import.FeesReportedByEbay = true;

            foreach (var flip in EarningsImporter.MapOrder(order))
            {
                // The seller's own figures survive a re-import. Someone who typed a shipping cost
                // or a cost basis against an imported sale must not have it wiped by importing
                // again — that turns a refresh into a way to lose work.
                if (existingByKey.TryGetValue((flip.OrderId, flip.LineItemId), out var existing))
                {
                    flip.Id = existing.Id;
                    flip.ShippingCost = existing.ShippingCost;
                    flip.OtherCosts = existing.OtherCosts;
                    flip.UnitCost = existing.UnitCost;
                    flip.Note = existing.Note;
                }

                try
                {
                    if (store.Upsert(flip)) import.LinesAdded++; else import.LinesUpdated++;
                    import.LinesImported++;
                    if (CostBasisStore.Find(knownCosts, flip.ListingId, flip.Sku) is not null) import.MatchedToCostBasis++;
                }
                catch (InvalidOperationException ex)
                {
                    import.LinesSkipped++;
                    log.Add("Warning", "An eBay order line couldn't be recorded", $"{flip.OrderId}/{flip.LineItemId}: {ex.Message}");
                }
            }
        }

        return import;
    }
}
