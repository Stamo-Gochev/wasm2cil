
module wasm.stringify_args
    open wasm.args

    let stringify_brtable t =
        sprintf "v: %A  other: %A" t.v t.other

    let stringify_memarg t =
        sprintf "align: %A offset: %A" t.align t.offset

    let stringify_callindirect t =
        sprintf "x: %A other: %A" t.x t.other
