namespace Rag.Core.Resilience;

/// <summary>Thrown when the circuit is open (or a half-open probe is already in flight) and the call is rejected.</summary>
public sealed class BrokenCircuitException(string message) : Exception(message);

/// <summary>Thrown when a single attempt exceeds its timeout.</summary>
public sealed class TimeoutRejectedException(string message, Exception? inner = null) : Exception(message, inner);
