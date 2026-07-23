namespace GPTino.BridgeContract.Tests;

public sealed class SelectionEventTests
{
    [Fact]
    public void SelectionChangedEventRoundTripsGrasshopperObjectsThroughAFrame()
    {
        var payload = new SelectionChangedEvent(
            [Guid.NewGuid()],
            "Facade::Panels",
            DateTimeOffset.UtcNow,
            [
                new GrasshopperSelectedObject(Guid.NewGuid(), "Python 3 Script", "H-Column Grid"),
                new GrasshopperSelectedObject(Guid.NewGuid(), "Number Slider", "Grid Spacing X (mm)")
            ]);

        var frame = BridgeFrame.Create(
            BridgeMessageKind.Event,
            BridgeMessageTypes.SelectionChanged,
            payload,
            DocumentTargetTests.CreateTarget());
        var decoded = frame.DeserializePayload<SelectionChangedEvent>();

        Assert.Equal(payload.RhinoObjectIds, decoded.RhinoObjectIds);
        Assert.Equal(payload.ActiveLayerName, decoded.ActiveLayerName);
        Assert.NotNull(decoded.GrasshopperObjects);
        Assert.Equal(2, decoded.GrasshopperObjects!.Count);
        Assert.Equal("H-Column Grid", decoded.GrasshopperObjects[0].NickName);
        Assert.Equal(payload.GrasshopperObjects![1].ObjectId, decoded.GrasshopperObjects[1].ObjectId);
    }

    [Fact]
    public void SelectionChangedEventOmittingGrasshopperObjectsStaysCompatible()
    {
        var payload = new SelectionChangedEvent([], null, DateTimeOffset.UtcNow);

        var frame = BridgeFrame.Create(
            BridgeMessageKind.Event,
            BridgeMessageTypes.SelectionChanged,
            payload,
            DocumentTargetTests.CreateTarget());
        var decoded = frame.DeserializePayload<SelectionChangedEvent>();

        Assert.Null(decoded.GrasshopperObjects);
    }

    [Fact]
    public void HubStoresLatestSelectionAndClearsItWithTheDocument()
    {
        var documentId = Guid.NewGuid();
        var observed = new List<int>();
        Action<Guid, IReadOnlyList<GrasshopperSelectedObject>> handler = (id, selection) =>
        {
            if (id == documentId)
            {
                observed.Add(selection.Count);
            }
        };
        BridgeProcessHub.GrasshopperSelectionChanged += handler;
        try
        {
            BridgeProcessHub.ObserveGrasshopperDocument(documentId, @"C:\bench\selection.gh");
            BridgeProcessHub.NotifyGrasshopperSelection(
                documentId,
                [new GrasshopperSelectedObject(Guid.NewGuid(), "Panel", "Notes")]);
            Assert.Single(BridgeProcessHub.GetGrasshopperSelection(documentId));

            BridgeProcessHub.NotifyGrasshopperSelection(documentId, []);
            Assert.Empty(BridgeProcessHub.GetGrasshopperSelection(documentId));

            BridgeProcessHub.ForgetGrasshopperDocument(documentId);
            Assert.Empty(BridgeProcessHub.GetGrasshopperSelection(documentId));
            Assert.Equal([1, 0], observed);
        }
        finally
        {
            BridgeProcessHub.GrasshopperSelectionChanged -= handler;
            BridgeProcessHub.ForgetGrasshopperDocument(documentId);
        }
    }
}
