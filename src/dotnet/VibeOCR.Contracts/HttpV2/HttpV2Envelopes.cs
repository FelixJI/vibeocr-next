// Wire envelopes returned by the events/result/cancel endpoints. Public and
// in Contracts so the HttpV2JsonContext can register them for source gen.
namespace VibeOCR.Contracts.HttpV2;

public sealed record EventsEnvelope(IReadOnlyList<StageEvent> Events);

public sealed record ResultsEnvelope(IReadOnlyList<ResultEntry> Results);

public sealed record CancelAck(CancelMode CancelMode);
