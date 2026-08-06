namespace GPTino.Contracts;

// The adaptive per-message router was removed (#48); these two enums are the surviving remnant.
// ModelProfile rides ModelSelection/EffectiveModelSnapshot into the panel projection and history
// records, and TaskClass is serialized to the panel as routingTaskClass. Both now carry the fixed
// Standard/StandardWrite values from the manual effort path.

public enum ModelProfile
{
    ReadFast,
    FastSafe,
    Standard,
    HighAssurance,
    Recovery,
}

public enum TaskClass
{
    ReadOnly,
    SimpleDeterministicWrite,
    StandardWrite,
    ComplexWrite,
    Recovery,
}
