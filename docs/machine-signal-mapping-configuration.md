# Machine Signal Mapping Configuration

## Purpose

Machine signal mapping separates protocol/hardware addresses from FactoryConnect business meaning.

A terminal or protocol address such as `DIN1`, `TR.1.DIN.4`, a PLC bit, or an MTConnect data item does not have a universal semantic meaning. Its meaning is established by the wiring, controller program, gateway configuration, and commissioning of one machine.

```text
Raw Machine / Protocol Observation
              ↓
Machine-Specific Signal Mapping
              ↓
Canonical FactoryConnect Signal
              ↓
State / Activity / Production / Metrics
```

## Architectural Invariant

A physical or protocol channel has no business meaning until a machine-specific configuration assigns one.

FactoryConnect therefore must not hard-code assumptions such as:

```text
DIN1 = Running
DIN2 = Fault
DIN3 = Part Count
DIN4 = Power On
```

Those assignments may be valid for one installation but different for another.

## Mapping Model

`MachineSignalMappingConfiguration` is scoped to one `MachineId` and contains `MachineSignalMappingDefinition` entries.

Each mapping identifies:

- source/protocol identity
- source address/channel
- canonical FactoryConnect signal key
- signal type
- optional digital inversion

Example:

```text
Machine: Legacy-01

Source: tcp-v
TR.1.DIN.1 → state.running
TR.1.DIN.2 → state.fault
TR.1.DIN.4 → state.power-on
```

Another machine may legitimately use:

```text
Machine: Legacy-02

Source: modbus
DI1 → state.power-on
DI2 → state.running
DI3 → state.fault
```

No change is required above the mapping layer.

## Raw and Canonical Observations

`MachineObservation` remains the immutable protocol-facing fact and retains:

- machine identity
- source
- address
- signal type
- raw value
- quality
- timestamp

`MachineSignalMapper` creates a separate `MappedMachineObservation` containing the canonical signal key while preserving source/address provenance.

Unknown addresses are not guessed. If no configured mapping exists, mapping returns no canonical observation.

## Digital Polarity

Industrial contacts may be active-high or active-low. `Invert` supports the first deterministic polarity requirement without embedding wiring assumptions in code.

```text
Raw DI2 = false
Mapping: state.fault, Invert = true
Canonical state.fault = true
```

Inversion is valid only for digital Boolean observations.

## Connection Configuration Is Separate

Signal mapping does not own transport details such as:

- IP address
- TCP port
- Modbus unit/device id
- MTConnect base URL
- polling interval
- reconnect policy

Those belong to connector/protocol configuration.

```text
Connection Configuration
Where and how do I read?
              ↓
Raw Observation
              ↓
Signal Mapping Configuration
What does this address mean?
              ↓
Canonical Signal
```

## MTConnect

MTConnect discovery and FactoryConnect semantic mapping remain separate responsibilities.

A future MTConnect connector can discover devices/data items from the Agent `/probe` endpoint. Discovered data items can then be suggested or assigned to canonical FactoryConnect signals through the same mapping model.

```text
MTConnect Agent /probe
        ↓
Signal Discovery
        ↓
Machine Signal Mapping
        ↓
Canonical Signals
```

FactoryConnect should not require operators to duplicate MTConnect device definitions manually when they can be discovered from the Agent.

## Persistence

FC-011 deliberately has no database dependency. Mapping configurations are plain domain/configuration objects and can be created in memory for tests and bootstrap setup.

When persistence and the setup UI are introduced, the same model can be stored as machine configuration data rather than application code.

A future setup workflow can become:

```text
Add Machine
    ↓
Select Connector
    ↓
Enter / discover connection
    ↓
Discover or enter source channels
    ↓
Map canonical signals
    ↓
Test
    ↓
Publish configuration
```

## Deferred

FC-011 does not introduce:

- database persistence
- configuration administration UI
- MTConnect `/probe` discovery implementation
- Modbus/TCP-V auto-discovery
- analog scaling
- threshold rules
- debounce/pulse processing
- state derivation

These concerns build on the stable signal-mapping boundary established here.

## Atomic operational status

An opt-in `state.operational` Enumeration mapping is supported for a configured
`MoxaStatusNormalization` profile. It owns state evaluation as one observation;
invalid or unavailable status returns Unknown instead of falling back to stale
Boolean evidence. It cannot be mixed with Boolean state-driving mappings in the
same stream. See [MOXA status normalization](moxa-status-normalization.md) for the
source contract, configuration, and the operational OFF-to-Stopped limitation.
