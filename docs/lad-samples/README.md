# LAD ground-truth samples (TIA Portal V20)

Real `block.Export()` output from the user's own project. These are the reference for element
names, pin names, UId patterns and instruction versions. `LadXmlBuilder` must not deviate
from them without a new export proving the alternative imports cleanly.

| File | Shows |
|---|---|
| 01-contact-coil-startup-ob.xml | Minimal OB: `Powerrail -> Coil.in`, `Coil.operand` via `GlobalVariable` access |
| 02-faults-fb-compare-or-ton-scoil.xml | `Eq`, `O` (Card=2) parallel merge, NC `Contact`, `TON` (+`time_type`), `TypedConstant T#5s`, `SCoil`, `ET -> OpenCon` |
| 03-fb-call-with-instance-db.xml | `<Call>` of an FB with `<Instance Scope="GlobalVariable">` |
| 04-program7-ob-ton-or-move-mixed.xml | Large mix (RCoil, Move, PBox, shared wires fanning out to several `in` pins) |
| 05-live-sealin-fb.xml | TIA re-export of a builder-generated seal-in FB (imported + compiled live) |
| 06-live-ton-multiinstance-fb.xml | Same for `TON` with `#Tmr` static instance (`TON_TIME`) |
| 07-live-contact-coil-fc.xml | Same for an FC (`Return` section, `Ret_Val` `Void`) |
| 08-live-contact-coil-ob.xml | Same for a ProgramCycle OB (only Input/Temp/Constant, explicit `Number`) |
| 09-live-compare-edge-move-tof-tp-fb.xml | `Gt/Eq/Ne/Ge/Le/Lt` (`SrcType`), `PBox`/`NBox` (`bit`), `Move` (`DisabledENO`), `TOF`, `TP` |
| 10-live-call-fc-with-params.xml | `Call` of an FC; input `IdentCon -> NameCon`, output `NameCon -> IdentCon` |
| 11-live-call-fb-instance-db.xml | `Call` of an FB with `Instance Scope="GlobalVariable"` |
| 12-live-call-fb-multi-instance.xml | `Call` of an FB with `Instance Scope="LocalVariable"` (multi-instance) |
| 13-live-call-no-params-ob.xml | Param-less FB call in an OB; TIA adds the parameter list and `OpenCon` for eno/outputs |
| 14-live-add-3-inputs-fb.xml ... 18-live-mod-fb.xml | `Add` (Card=3), `Sub`, `Mul`, `Div`, `Mod`: `DisabledENO`, `AutomaticTyped SrcType` |
| 19-live-normalize-scale-x-fb.xml | `Normalize` -> `Scale_X` chained through `eno` |
| 20-live-sr-rs-fb.xml | `Sr` (`s`, `r1`) and `Rs` (`r`, `s1`) with a memory `operand` |
| 21-live-ctu-ctd-fb.xml | `CTU`/`CTD` with multi-instance, `PV`, `CV` open, output `Q` |
| 22-live-system-function-wr-sys-t-fb.xml | `WR_SYS_T` as a `Part` with `Version` and `date_type` |

Format facts confirmed here:
- `FlgNet` namespace is `.../NetworkSource/FlgNet/v5` in V20.
- `Contact`, `Coil`, `SCoil`, `RCoil` pins: `in`, `operand`, `out`. NC = child `<Negated Name="operand" />`.
- `TON` is `Version="1.0"`; pins `IN`, `PT`, `Q`, `ET`.
- A source driving several pins is ONE `<Wire>` with several `<NameCon>` destinations.
- Block-level interface lives in `<AttributeList><Interface><Sections xmlns=".../Interface/v5">`.
