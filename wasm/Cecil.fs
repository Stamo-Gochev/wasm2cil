
module wasm.cecil

    open Mono.Cecil
    open Mono.Cecil.Cil

    open wasm.def_basic
    open wasm.def
    open wasm.def_instr
    open wasm.module_index
    open wasm.instr_stack
    open wasm.import
    open wasm.errors

    let mem_page_size = 64 * 1024

    type ProfileSetting =
        | Yes of System.Reflection.Assembly
        | No

    type TraceSetting =
        | Yes of System.Reflection.Assembly
        | No

    type MemorySetting =
        | AlwaysImportPairFrom of string
        | Default

    type CompilerSettings = {
        profile : ProfileSetting
        trace : TraceSetting
        env : System.Reflection.Assembly option
        memory : MemorySetting
        }

    type BasicTypes = {
        typ_void : TypeReference
        typ_i32 : TypeReference
        typ_i64 : TypeReference
        typ_f32 : TypeReference
        typ_f64 : TypeReference
        typ_intptr : TypeReference
        typ_object : TypeReference
        }

    let cecil_valtype bt vt =
        match vt with
        | I32 -> bt.typ_i32
        | I64 -> bt.typ_i64
        | F32 -> bt.typ_f32
        | F64 -> bt.typ_f64

    type ParamRef = {
        typ : ValType;
        def_param : ParameterDefinition;
        }

    type LocalRef = {
        typ : ValType;
        def_var : VariableDefinition;
        }

    type ParamOrVar =
        | ParamRef of ParamRef
        | LocalRef of LocalRef

    type GlobalRefImported = {
        glob : ImportedGlobal
        field : FieldReference
        }

    type GlobalRefInternal = {
        glob : InternalGlobal
        field : FieldDefinition
        }

    type GlobalStuff = 
        | GlobalRefImported of GlobalRefImported
        | GlobalRefInternal of GlobalRefInternal

    type MethodRefImported = {
        func : ImportedFunc
        method : MethodReference
        }

    type MethodRefInternal = {
        func : InternalFunc
        method : MethodDefinition
        }

    type MethodStuff = 
        | MethodRefImported of MethodRefImported
        | MethodRefInternal of MethodRefInternal

    type ProfileHooks = {
        profile_enter : MethodReference
        profile_exit : MethodReference
        }

    type TraceHooks = {
        trace_enter : MethodReference
        trace_exit_value : MethodReference
        trace_exit_void : MethodReference
        trace_grow_mem : MethodReference
        }

    type GenContext = {
        types: FuncType[]
        md : ModuleDefinition
        mem : FieldReference
        mem_size : FieldReference
        mem_grow : MethodReference
        tbl_lookup : MethodDefinition option
        a_globals : GlobalStuff[]
        a_methods : MethodStuff[]
        bt : BasicTypes
        trace : TraceHooks option
        profile : ProfileHooks option
        }

    type DataStuff = {
        item : DataItem
        name : string
        resource : EmbeddedResource
        }

    let get_function_name fidx f =
        match f with
        | ImportedFunc i -> sprintf "%s.%s" i.m i.name
        | InternalFunc i -> 
            match i.name with
            | Some s -> s
            | None -> sprintf "func_%d" fidx

    type MyStack<'a> = {
            mutable top : 'a list
        }

    let pop stk =
        //printfn "before pop: %A" stk.top
        match stk.top with
        | [] -> raise (OperandStackUnderflow "TODO")
        | v::tail ->
            stk.top <- tail
            v

    let try_peek stk =
        match stk.top with
        | [] -> None
        | v::tail -> Some v

    let peek stk =
        match stk.top with
        | [] -> raise (OperandStackUnderflow "TODO")
        | v::tail -> v

    let push stk v =
        stk.top <- v :: stk.top
        //printfn "after push: %A" stk.top

    let new_stack_empty () =
        { top = [] }

    let new_stack_one v =
        { top = [ v ] }

    type BlockInfo = {
        result : ValType option
        opstack : MyStack<ValType>
        label : Mono.Cecil.Cil.Instruction
        mutable stack_polymorphic : bool
        }

    // TODO should reverse this, 
    // since all the union cases have the same data,
    // just do BlockInfo.kind
    type CodeBlock =
        | CB_Body of BlockInfo
        | CB_Block of BlockInfo
        | CB_Loop of BlockInfo
        | CB_If of BlockInfo
        | CB_Else of BlockInfo

    let get_blockinfo blk =
        match blk with
        | CB_Body b -> b
        | CB_Block b -> b
        | CB_Loop b -> b
        | CB_If b -> b
        | CB_Else b -> b

    let get_block_string blk =
        let k =
            match blk with
            | CB_Body b -> "body"
            | CB_Block b -> "block"
            | CB_Loop b -> "loop"
            | CB_If b -> "if"
            | CB_Else b -> "else"
        let bi = get_blockinfo blk
        let t = 
            match bi.result with
            | Some vt -> (vt.ToString())
            | None -> "void"
        let poly = if bi.stack_polymorphic then " - unreachable" else ""
        sprintf "%s %s%s"k t poly

    let make_tmp (method : MethodDefinition) (t : TypeReference) =
        let v = new VariableDefinition(t)
        method.Body.Variables.Add(v)
        v

    let gen_body_popcnt_i32 bt (il : ILProcessor) (f_make_tmp : TypeReference -> VariableDefinition) =
        // x -= x >> 1 & 0x55555555;
        il.Append(il.Create(OpCodes.Dup))
        il.Append(il.Create(OpCodes.Ldc_I4, 1))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Ldc_I4, 0x55555555))
        il.Append(il.Create(OpCodes.And))
        il.Append(il.Create(OpCodes.Sub))

        // x = (x & 0x33333333) + (x >> 2 & 0x33333333);
        let t1 = f_make_tmp (cecil_valtype bt I32)
        il.Append(il.Create(OpCodes.Stloc, t1))
        il.Append(il.Create(OpCodes.Ldloc, t1))
        il.Append(il.Create(OpCodes.Ldc_I4, 0x33333333))
        il.Append(il.Create(OpCodes.And))
        il.Append(il.Create(OpCodes.Ldloc, t1))
        il.Append(il.Create(OpCodes.Ldc_I4, 2))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Ldc_I4, 0x33333333))
        il.Append(il.Create(OpCodes.And))
        il.Append(il.Create(OpCodes.Add))

        // x = (x >> 4) + x & 0x0f0f0f0f;
        il.Append(il.Create(OpCodes.Dup))
        il.Append(il.Create(OpCodes.Ldc_I4, 4))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Add))
        il.Append(il.Create(OpCodes.Ldc_I4, 0x0f0f0f0f))
        il.Append(il.Create(OpCodes.And))

        // x += x >> 8;
        il.Append(il.Create(OpCodes.Dup))
        il.Append(il.Create(OpCodes.Ldc_I4, 8))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Add))

        // x += x >> 16;
        il.Append(il.Create(OpCodes.Dup))
        il.Append(il.Create(OpCodes.Ldc_I4, 16))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Add))

        // return x & 0x0000003f;
        il.Append(il.Create(OpCodes.Ldc_I4, 0x0000003f))
        il.Append(il.Create(OpCodes.And))

    let gen_body_popcnt_i64 bt (il : ILProcessor) (f_make_tmp : TypeReference -> VariableDefinition) =
        // x -= (x >> 1) & 0x5555555555555555L;
        il.Append(il.Create(OpCodes.Dup))
        il.Append(il.Create(OpCodes.Ldc_I4, 1))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Ldc_I8, 0x5555555555555555L))
        il.Append(il.Create(OpCodes.And))
        il.Append(il.Create(OpCodes.Sub))

        // x = (x & 0x3333333333333333L) + ((x >> 2) & 0x3333333333333333L);
        let t1 = f_make_tmp (cecil_valtype bt I64)
        il.Append(il.Create(OpCodes.Stloc, t1))
        il.Append(il.Create(OpCodes.Ldloc, t1))
        il.Append(il.Create(OpCodes.Ldc_I8, 0x3333333333333333L))
        il.Append(il.Create(OpCodes.And))
        il.Append(il.Create(OpCodes.Ldloc, t1))
        il.Append(il.Create(OpCodes.Ldc_I4, 2))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Ldc_I8, 0x3333333333333333L))
        il.Append(il.Create(OpCodes.And))
        il.Append(il.Create(OpCodes.Add))

        // x = (x + (x >> 4)) & 0x0f0f0f0f0f0f0f0fL;
        il.Append(il.Create(OpCodes.Dup))
        il.Append(il.Create(OpCodes.Ldc_I4, 4))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Add))
        il.Append(il.Create(OpCodes.Ldc_I8, 0x0f0f0f0f0f0f0f0fL))
        il.Append(il.Create(OpCodes.And))

        // return (x * 0x0101010101010101L) >> 56
        il.Append(il.Create(OpCodes.Ldc_I8, 0x0101010101010101L))
        il.Append(il.Create(OpCodes.Mul))
        il.Append(il.Create(OpCodes.Ldc_I4, 56))
        il.Append(il.Create(OpCodes.Shr_Un))

    let gen_body_clz_i64 bt (il : ILProcessor) (f_make_tmp : TypeReference -> VariableDefinition) =
        // https://stackoverflow.com/questions/10439242/count-leading-zeroes-in-an-int32
        // do the smearing

        // x |= x >> 1; 
        il.Append(il.Create(OpCodes.Dup))
        il.Append(il.Create(OpCodes.Ldc_I4, 1))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Or))

        // x |= x >> 2; 
        il.Append(il.Create(OpCodes.Dup))
        il.Append(il.Create(OpCodes.Ldc_I4, 2))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Or))

        // x |= x >> 4; 
        il.Append(il.Create(OpCodes.Dup))
        il.Append(il.Create(OpCodes.Ldc_I4, 4))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Or))

        // x |= x >> 8; 
        il.Append(il.Create(OpCodes.Dup))
        il.Append(il.Create(OpCodes.Ldc_I4, 8))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Or))

        // x |= x >> 16; 
        il.Append(il.Create(OpCodes.Dup))
        il.Append(il.Create(OpCodes.Ldc_I4, 16))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Or))

        // x |= x >> 32; 
        il.Append(il.Create(OpCodes.Dup))
        il.Append(il.Create(OpCodes.Ldc_I4, 32))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Or))

        gen_body_popcnt_i64 bt il f_make_tmp

        il.Append(il.Create(OpCodes.Neg))
        il.Append(il.Create(OpCodes.Ldc_I8, 64L))
        il.Append(il.Create(OpCodes.Add))

    let gen_body_ctz_i64 bt (il : ILProcessor) (f_make_tmp : TypeReference -> VariableDefinition) =
        let i = f_make_tmp (cecil_valtype bt I64)
        il.Append(il.Create(OpCodes.Stloc, i))

        let count = f_make_tmp (cecil_valtype bt I32)

        // init count to 64, for when i is 0
        il.Append(il.Create(OpCodes.Ldc_I4, 64))
        il.Append(il.Create(OpCodes.Stloc, count))

        let lab_done = il.Create(OpCodes.Nop)

        // if i is 0, skip the loop
        il.Append(il.Create(OpCodes.Ldloc, i))
        il.Append(il.Create(OpCodes.Brfalse, lab_done))

        // result count to 0 so we can increment it in the loop
        il.Append(il.Create(OpCodes.Ldc_I4, 0))
        il.Append(il.Create(OpCodes.Stloc, count))

        // while ((i & 0x01L) == 0)
        let lab_top_of_loop = il.Create(OpCodes.Nop)
        il.Append(lab_top_of_loop)
        il.Append(il.Create(OpCodes.Ldloc, i))
        il.Append(il.Create(OpCodes.Ldc_I8, 0x01L))
        il.Append(il.Create(OpCodes.And))
        il.Append(il.Create(OpCodes.Brtrue, lab_done))

        // i = i >> 1;
        il.Append(il.Create(OpCodes.Ldloc, i))
        il.Append(il.Create(OpCodes.Ldc_I4, 0x01))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Stloc, i))

        // count++;
        il.Append(il.Create(OpCodes.Ldloc, count))
        il.Append(il.Create(OpCodes.Ldc_I4, 1))
        il.Append(il.Create(OpCodes.Add))
        il.Append(il.Create(OpCodes.Stloc, count))

        il.Append(il.Create(OpCodes.Br, lab_top_of_loop))

        il.Append(lab_done)
        il.Append(il.Create(OpCodes.Ldloc, count))

    let gen_body_ctz_i32 bt (il : ILProcessor) (f_make_tmp : TypeReference -> VariableDefinition) =
        let i = f_make_tmp (cecil_valtype bt I32)
        il.Append(il.Create(OpCodes.Stloc, i))

        let count = f_make_tmp (cecil_valtype bt I32)

        // init count to 32, for when i is 0
        il.Append(il.Create(OpCodes.Ldc_I4, 32))
        il.Append(il.Create(OpCodes.Stloc, count))

        let lab_done = il.Create(OpCodes.Nop)

        // if i is 0, skip the loop
        il.Append(il.Create(OpCodes.Ldloc, i))
        il.Append(il.Create(OpCodes.Brfalse, lab_done))

        // result count to 0 so we can increment it in the loop
        il.Append(il.Create(OpCodes.Ldc_I4, 0))
        il.Append(il.Create(OpCodes.Stloc, count))

        // while ((i & 0x01L) == 0)
        let lab_top_of_loop = il.Create(OpCodes.Nop)
        il.Append(lab_top_of_loop)
        il.Append(il.Create(OpCodes.Ldloc, i))
        il.Append(il.Create(OpCodes.Ldc_I4, 0x01))
        il.Append(il.Create(OpCodes.And))
        il.Append(il.Create(OpCodes.Brtrue, lab_done))

        // i = i >> 1;
        il.Append(il.Create(OpCodes.Ldloc, i))
        il.Append(il.Create(OpCodes.Ldc_I4, 0x01))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Stloc, i))

        // count++;
        il.Append(il.Create(OpCodes.Ldloc, count))
        il.Append(il.Create(OpCodes.Ldc_I4, 1))
        il.Append(il.Create(OpCodes.Add))
        il.Append(il.Create(OpCodes.Stloc, count))

        il.Append(il.Create(OpCodes.Br, lab_top_of_loop))

        il.Append(lab_done)
        il.Append(il.Create(OpCodes.Ldloc, count))

    let gen_body_clz_i32 bt (il : ILProcessor) (f_make_tmp : TypeReference -> VariableDefinition) =
        // https://stackoverflow.com/questions/10439242/count-leading-zeroes-in-an-int32
        // do the smearing

        // x |= x >> 1; 
        il.Append(il.Create(OpCodes.Dup))
        il.Append(il.Create(OpCodes.Ldc_I4, 1))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Or))

        // x |= x >> 2; 
        il.Append(il.Create(OpCodes.Dup))
        il.Append(il.Create(OpCodes.Ldc_I4, 2))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Or))

        // x |= x >> 4; 
        il.Append(il.Create(OpCodes.Dup))
        il.Append(il.Create(OpCodes.Ldc_I4, 4))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Or))

        // x |= x >> 8; 
        il.Append(il.Create(OpCodes.Dup))
        il.Append(il.Create(OpCodes.Ldc_I4, 8))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Or))

        // x |= x >> 16; 
        il.Append(il.Create(OpCodes.Dup))
        il.Append(il.Create(OpCodes.Ldc_I4, 16))
        il.Append(il.Create(OpCodes.Shr_Un))
        il.Append(il.Create(OpCodes.Or))

        gen_body_popcnt_i32 bt il f_make_tmp

        il.Append(il.Create(OpCodes.Neg))
        il.Append(il.Create(OpCodes.Ldc_I4, 32))
        il.Append(il.Create(OpCodes.Add))

    let check_instr ctx (a_locals : ParamOrVar[]) blocks op =
        let cur_block = peek blocks
        let cur_blockinfo = get_blockinfo cur_block
        let cur_opstack = cur_blockinfo.opstack

        let stack_info = get_instruction_stack_info op
        //printfn "    stack_info: %A" stack_info

        let type_check should actual =
            if actual <> should then
                let name = wasm.instr_name.get_instruction_name op
                let s = sprintf "%s: arg is %A but should be %A" name actual should
                raise (WrongOperandType s)
        let handle_stack_for_call ftype =
            if ftype.parms.Length > 0 then
                for i = (ftype.parms.Length - 1) downto 0 do
                    let should = ftype.parms.[i]
                    let actual = pop cur_opstack
                    type_check should actual
            function_result_type ftype

        match stack_info with
        | NoArgs rt -> rt
        | OneArg { rtype = rt; arg = t1; } ->
            let arg1 = pop cur_opstack
            type_check arg1 t1
            rt
        | TwoArgs { rtype = rt; arg1 = t1; arg2 = t2; } ->
            let arg2 = pop cur_opstack
            let arg1 = pop cur_opstack
            type_check t1 arg1
            type_check t2 arg2
            rt
        | SpecialCaseBr ->
            cur_blockinfo.stack_polymorphic <- true
            None
        | SpecialCaseBrTable ->
            let arg1 = pop cur_opstack
            type_check arg1 I32
            cur_blockinfo.stack_polymorphic <- true
            None
        | SpecialCaseReturn ->
            cur_blockinfo.stack_polymorphic <- true
            None
        | SpecialCaseUnreachable ->
            cur_blockinfo.stack_polymorphic <- true
            None
        | SpecialCaseBlock t ->
            None
        | SpecialCaseIf t ->
            None
        | SpecialCaseElse ->
            None
        | SpecialCaseLoop t ->
            None
        | SpecialCaseEnd ->
            // TODO how to deal with stack_polymorphic mode here?
            //printfn "in stack code for End: blocks: %A" blocks
            //printfn "    and opstack is %A" cur_opstack
            let bi = get_blockinfo cur_block
            match bi.result with
            | Some t ->
                //printfn "    block type is %A" t
                let arg = pop cur_opstack
                //printfn "    after pop block result, opstack is %A" cur_opstack
                type_check arg t
            | None -> ()
            match try_peek cur_opstack with
            | Some _ -> raise (ExtraBlockResult "TODO")
            | None -> ()
            bi.result
        | SpecialCaseDrop ->
            pop cur_opstack |> ignore
            None
        | SpecialCaseSelect ->
            let arg3 = pop cur_opstack
            let arg2 = pop cur_opstack
            let arg1 = pop cur_opstack
            type_check arg3 I32
            if arg1 <> arg2 then failwith "select types must match"
            Some arg1
        | SpecialCaseCall (FuncIdx fidx) ->
            let fn = ctx.a_methods.[int fidx]
            let ftype = 
                match fn with
                | MethodRefImported mf -> mf.func.typ
                | MethodRefInternal mf -> mf.func.typ
            handle_stack_for_call ftype
        | SpecialCaseCallIndirect calli ->
            let arg1 = pop cur_opstack
            type_check arg1 I32
            let (TypeIdx tidx) = calli.typeidx
            let ftype = ctx.types.[int tidx]
            handle_stack_for_call ftype
        | SpecialCaseLocalSet (LocalIdx i) ->
            let loc = a_locals.[int i]
            let typ =
                match loc with
                | ParamRef { typ = t } -> t
                | LocalRef { typ = t } -> t
            let arg = pop cur_opstack
            type_check arg typ
            None
        | SpecialCaseLocalGet (LocalIdx i) ->
            let loc = a_locals.[int i]
            let typ =
                match loc with
                | ParamRef { typ = t } -> t
                | LocalRef { typ = t } -> t
            Some typ
        | SpecialCaseGlobalSet (GlobalIdx i) ->
            let g = ctx.a_globals.[int i]
            let typ = 
                match g with
                | GlobalRefImported mf -> mf.glob.typ.typ
                | GlobalRefInternal mf -> mf.glob.item.globaltype.typ
            let arg = pop cur_opstack
            type_check arg typ
            None
        | SpecialCaseGlobalGet (GlobalIdx i) ->
            let g = ctx.a_globals.[int i]
            let typ = 
                match g with
                | GlobalRefImported mf -> mf.glob.typ.typ
                | GlobalRefInternal mf -> mf.glob.item.globaltype.typ
            Some typ
        | SpecialCaseLocalTee (LocalIdx i) ->
            let loc = a_locals.[int i]
            let typ =
                match loc with
                | ParamRef { typ = t } -> t
                | LocalRef { typ = t } -> t
            let arg = pop cur_opstack
            type_check arg typ
            push cur_opstack arg
            None

    let gen_unreachable ctx blocks (il : ILProcessor) op =
        match op with
        | Block t -> 
            let lab = il.Create(OpCodes.Nop)
            let blk = CB_Block { label = lab; opstack = new_stack_empty (); result = t; stack_polymorphic = true; }
            push blocks blk
            None
        | Loop t -> 
            let lab = il.Create(OpCodes.Nop)
            let blk = CB_Loop { label = lab; opstack = new_stack_empty (); result = t; stack_polymorphic = true; }
            push blocks blk
            il.Append(lab)
            None
        | If t -> 
            let lab = il.Create(OpCodes.Nop)
            let blk = CB_If { label = lab; opstack = new_stack_empty (); result = t; stack_polymorphic = true; }
            push blocks blk
            None
        | Else -> 
            // first, end the if block
            let blk_typ =
                match pop blocks with
                | CB_If { label = lab; result = r; } -> 
                    il.Append(lab)
                    r
                | _ -> failwith "bad nest"

            let lab = il.Create(OpCodes.Nop)
            let blk = CB_Else { label = lab; opstack = new_stack_empty (); result = blk_typ; stack_polymorphic = true; }
            push blocks blk
            None
        | End -> 
            let b = pop blocks
            let bi = get_blockinfo b
            match b with
            | CB_Body { label = lab } -> il.Append(lab) // TODO only gen this if it is needed ?
            | CB_Block { label = lab } -> il.Append(lab)
            | CB_Loop _ -> () // loop label was at the top
            | CB_If { label = lab } -> il.Append(lab)
            | CB_Else { label = lab } -> il.Append(lab)
            bi.result
        | _ -> None

    let gen_instr ctx (a_locals : ParamOrVar[]) blocks result_type body (il : ILProcessor) f_make_tmp op =
        let get_label_from_block blk =
            match blk with
            | CB_Body _ -> failwith "branch not allowed outside block"
            | CB_Block { label = s } -> s
            | CB_Loop { label = s } -> s
            | CB_If { label = s } -> s
            | CB_Else { label = s } -> s

        let find_branch_target i =
            //printfn "find_branch_target"

            let (LabelIdx i) = i
            let i = int i
            let blk = List.item i blocks.top
            let lab = get_label_from_block blk
            lab

        let prep_addr (m : MemArg) =
            // the address operand should be on the stack
            il.Append(il.Create(OpCodes.Ldsfld, ctx.mem))
            il.Append(il.Create(OpCodes.Add))
            if m.offset <> 0u then
                il.Append(il.Create(OpCodes.Ldc_I4, int m.offset))
                il.Append(il.Create(OpCodes.Add))

        let load m op =
            prep_addr m
            il.Append(il.Create(op))

        let store m op (tmp : VariableDefinition) =
            il.Append(il.Create(OpCodes.Stloc, tmp)) // pop v into tmp
            prep_addr m
            il.Append(il.Create(OpCodes.Ldloc, tmp)) // put v back
            il.Append(il.Create(op))

        let todo q =
            let msg = sprintf "TODO %A" q
            printfn "%s" msg
            let ref_typ_e = ctx.md.ImportReference(typeof<System.Exception>.GetConstructor([| typeof<string> |]))
            il.Append(il.Create(OpCodes.Ldstr, msg))
            il.Append(il.Create(OpCodes.Newobj, ref_typ_e))
            il.Append(il.Create(OpCodes.Throw))

        match op with
        | Block t -> 
            let lab = il.Create(OpCodes.Nop)
            let blk = CB_Block { label = lab; opstack = new_stack_empty (); result = t; stack_polymorphic = false; }
            push blocks blk
        | Loop t -> 
            let lab = il.Create(OpCodes.Nop)
            let blk = CB_Loop { label = lab; opstack = new_stack_empty (); result = t; stack_polymorphic = false; }
            push blocks blk
            il.Append(lab)
        | If t -> 
            let lab = il.Create(OpCodes.Nop)
            let blk = CB_If { label = lab; opstack = new_stack_empty (); result = t; stack_polymorphic = false; }
            push blocks blk
        | Else -> 
            // first, end the if block
            let blk_typ =
                match pop blocks with
                | CB_If { label = lab; result = r; } -> 
                    il.Append(lab)
                    r
                | _ -> failwith "bad nest"

            let lab = il.Create(OpCodes.Nop)
            let blk = CB_Else { label = lab; opstack = new_stack_empty (); result = blk_typ; stack_polymorphic = false; }
            push blocks blk
        | End -> 
            match pop blocks with
            | CB_Body { label = lab } -> il.Append(lab) // TODO only gen this if it is needed ?
            | CB_Block { label = lab } -> il.Append(lab)
            | CB_Loop _ -> () // loop label was at the top
            | CB_If { label = lab } -> il.Append(lab)
            | CB_Else { label = lab } -> il.Append(lab)
        | Return ->
            il.Append(il.Create(OpCodes.Br, body.label))
        | Nop -> il.Append(il.Create(OpCodes.Nop))
        | Br i ->
            let lab = find_branch_target i
            il.Append(il.Create(OpCodes.Br, lab))
        | BrIf i ->
            let lab = find_branch_target i
            il.Append(il.Create(OpCodes.Brtrue, lab))
        | BrTable m ->
            let q = Array.map (fun i -> find_branch_target i) m.v
            il.Append(il.Create(OpCodes.Switch, q))
            let lab = find_branch_target m.other
            il.Append(il.Create(OpCodes.Br, lab))

        | Call (FuncIdx fidx) ->
            let fn = ctx.a_methods.[int fidx]
            match fn with
            | MethodRefImported mf ->
                il.Append(il.Create(OpCodes.Call, mf.method))
            | MethodRefInternal mf ->
                il.Append(il.Create(OpCodes.Call, mf.method))

        | CallIndirect _ ->
            let cs =
                let stack_info = get_instruction_stack_info op
                match stack_info with
                | SpecialCaseCallIndirect calli ->
                    let cs = 
                        match result_type with
                        | Some t -> CallSite(cecil_valtype ctx.bt t)
                        | None -> CallSite(ctx.bt.typ_void)
                    let (TypeIdx tidx) = calli.typeidx
                    let ftype = ctx.types.[int tidx]
                    for a in ftype.parms do
                        cs.Parameters.Add(ParameterDefinition(cecil_valtype ctx.bt a))
                    cs
                | _ -> failwith "should not happen"
            match ctx.tbl_lookup with
            | Some f -> il.Append(il.Create(OpCodes.Call, f))
            | None -> failwith "illegal to use CallIndirect with no table"
            il.Append(il.Create(OpCodes.Calli, cs))

        | GlobalGet (GlobalIdx idx) ->
            let g = ctx.a_globals.[int idx]
            match g with
            | GlobalRefImported mf ->
                il.Append(il.Create(OpCodes.Ldsfld, mf.field))
            | GlobalRefInternal mf ->
                il.Append(il.Create(OpCodes.Ldsfld, mf.field))

        | GlobalSet (GlobalIdx idx) ->
            let g = ctx.a_globals.[int idx]
            match g with
            | GlobalRefImported mf ->
                il.Append(il.Create(OpCodes.Stsfld, mf.field))
            | GlobalRefInternal mf ->
                il.Append(il.Create(OpCodes.Stsfld, mf.field))

        | LocalTee (LocalIdx i) -> 
            // TODO want to use Stloc_0 and the like, when we can.
            il.Append(il.Create(OpCodes.Dup))
            let loc = a_locals.[int i]
            match loc with
            | ParamRef { def_param = n } -> il.Append(il.Create(OpCodes.Starg, n))
            | LocalRef { def_var = n } -> il.Append(il.Create(OpCodes.Stloc, n))

        | LocalSet (LocalIdx i) -> 
            // TODO want to use Stloc_0 and the like, when we can.
            let loc = a_locals.[int i]
            match loc with
            | ParamRef { def_param = n } -> il.Append(il.Create(OpCodes.Starg, n))
            | LocalRef { def_var = n } -> il.Append(il.Create(OpCodes.Stloc, n))

        | LocalGet (LocalIdx i) -> 
            // TODO want to use Ldarg_0 and the like, when we can.
            // any chance Cecil is already doing this behind the scenes?
            let loc = a_locals.[int i]
            match loc with
            | ParamRef { def_param = n } -> il.Append(il.Create(OpCodes.Ldarg, n))
            | LocalRef { def_var = n } -> il.Append(il.Create(OpCodes.Ldloc, n))

        | I32Load m -> load m OpCodes.Ldind_I4
        | I64Load m -> load m OpCodes.Ldind_I8
        | F32Load m -> load m OpCodes.Ldind_R4
        | F64Load m -> load m OpCodes.Ldind_R8
        | I32Load8S m -> load m OpCodes.Ldind_I1
        | I32Load8U m -> load m OpCodes.Ldind_U1
        | I32Load16S m -> load m OpCodes.Ldind_I2
        | I32Load16U m -> load m OpCodes.Ldind_U2
        | I64Load8S m -> 
            load m OpCodes.Ldind_I1
            il.Append(il.Create(OpCodes.Conv_I8))
        | I64Load8U m -> 
            load m OpCodes.Ldind_U1
            il.Append(il.Create(OpCodes.Conv_I8))
        | I64Load16S m -> 
            load m OpCodes.Ldind_I2
            il.Append(il.Create(OpCodes.Conv_I8))
        | I64Load16U m -> 
            load m OpCodes.Ldind_U2
            il.Append(il.Create(OpCodes.Conv_I8))
        | I64Load32S m -> 
            load m OpCodes.Ldind_I4
            il.Append(il.Create(OpCodes.Conv_I8))
        | I64Load32U m -> 
            load m OpCodes.Ldind_U4
            il.Append(il.Create(OpCodes.Conv_I8)) // TODO is this right?

        | I32Store m -> store m OpCodes.Stind_I4 (f_make_tmp ctx.bt.typ_i32)
        | I64Store m -> store m OpCodes.Stind_I8 (f_make_tmp ctx.bt.typ_i64)
        | F32Store m -> store m OpCodes.Stind_R4 (f_make_tmp ctx.bt.typ_f32)
        | F64Store m -> store m OpCodes.Stind_R8 (f_make_tmp ctx.bt.typ_f64)
        | I32Store8 m -> store m OpCodes.Stind_I1 (f_make_tmp ctx.bt.typ_i32)
        | I32Store16 m -> store m OpCodes.Stind_I2 (f_make_tmp ctx.bt.typ_i32)
        | I64Store8 m -> store m OpCodes.Stind_I1 (f_make_tmp ctx.bt.typ_i64)
        | I64Store16 m -> store m OpCodes.Stind_I2 (f_make_tmp ctx.bt.typ_i64)
        | I64Store32 m -> store m OpCodes.Stind_I4 (f_make_tmp ctx.bt.typ_i64)

        | I32Const i -> 
            match i with
            | -1 -> il.Append(il.Create(OpCodes.Ldc_I4_M1))
            | 0 -> il.Append(il.Create(OpCodes.Ldc_I4_0))
            | 1 -> il.Append(il.Create(OpCodes.Ldc_I4_1))
            | 2 -> il.Append(il.Create(OpCodes.Ldc_I4_2))
            | 3 -> il.Append(il.Create(OpCodes.Ldc_I4_3))
            | 4 -> il.Append(il.Create(OpCodes.Ldc_I4_4))
            | 5 -> il.Append(il.Create(OpCodes.Ldc_I4_5))
            | 6 -> il.Append(il.Create(OpCodes.Ldc_I4_6))
            | 7 -> il.Append(il.Create(OpCodes.Ldc_I4_7))
            | 8 -> il.Append(il.Create(OpCodes.Ldc_I4_8))
            | _ -> il.Append(il.Create(OpCodes.Ldc_I4, i))
        | I64Const i -> il.Append(il.Create(OpCodes.Ldc_I8, i))
        | F32Const i -> il.Append(il.Create(OpCodes.Ldc_R4, i))
        | F64Const i -> il.Append(il.Create(OpCodes.Ldc_R8, i))

        | I32Add | I64Add | F32Add | F64Add -> il.Append(il.Create(OpCodes.Add))
        | I32Mul | I64Mul | F32Mul | F64Mul -> il.Append(il.Create(OpCodes.Mul))
        | I32Sub | I64Sub | F32Sub | F64Sub -> il.Append(il.Create(OpCodes.Sub))
        | I32DivS | I64DivS | F32Div | F64Div -> il.Append(il.Create(OpCodes.Div))
        | I32DivU | I64DivU -> il.Append(il.Create(OpCodes.Div_Un))

        | F64Abs ->
            let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Abs", [| typeof<double> |] ))
            il.Append(il.Create(OpCodes.Call, ext))
        | F64Sqrt ->
            let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Sqrt", [| typeof<double> |] ))
            il.Append(il.Create(OpCodes.Call, ext))
        | F64Ceil ->
            let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Ceiling", [| typeof<double> |] ))
            il.Append(il.Create(OpCodes.Call, ext))
        | F64Floor ->
            let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Floor", [| typeof<double> |] ))
            il.Append(il.Create(OpCodes.Call, ext))
        | F64Trunc ->
            let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Truncate", [| typeof<double> |] ))
            il.Append(il.Create(OpCodes.Call, ext))
        | F64Nearest ->
            let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Round", [| typeof<double> |] ))
            il.Append(il.Create(OpCodes.Call, ext))
        | F64Min ->
            let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Min", [| typeof<double>; typeof<double> |] ))
            il.Append(il.Create(OpCodes.Call, ext))
        | F64Max ->
            let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Max", [| typeof<double>; typeof<double> |] ))
            il.Append(il.Create(OpCodes.Call, ext))

        | F32Abs ->
            let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Abs", [| typeof<float32> |] ))
            il.Append(il.Create(OpCodes.Call, ext))
        | F32Sqrt ->
            let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Sqrt", [| typeof<float32> |] ))
            il.Append(il.Create(OpCodes.Call, ext))
        | F32Ceil ->
            let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Ceiling", [| typeof<float32> |] ))
            il.Append(il.Create(OpCodes.Call, ext))
        | F32Floor ->
            let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Floor", [| typeof<float32> |] ))
            il.Append(il.Create(OpCodes.Call, ext))
        | F32Trunc ->
            let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Truncate", [| typeof<float32> |] ))
            il.Append(il.Create(OpCodes.Call, ext))
        | F32Nearest ->
            let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Round", [| typeof<float32> |] ))
            il.Append(il.Create(OpCodes.Call, ext))
        | F32Min ->
            let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Min", [| typeof<float32>; typeof<float32> |] ))
            il.Append(il.Create(OpCodes.Call, ext))
        | F32Max ->
            let ext = ctx.md.ImportReference(typeof<System.Math>.GetMethod("Max", [| typeof<float32>; typeof<float32> |] ))
            il.Append(il.Create(OpCodes.Call, ext))

        | I32Eqz ->
            il.Append(il.Create(OpCodes.Ldc_I4_0))
            il.Append(il.Create(OpCodes.Ceq))
            
        | I64Eqz ->
            il.Append(il.Create(OpCodes.Ldc_I8, 0L))
            il.Append(il.Create(OpCodes.Ceq))
            
        | I32LtS | I64LtS | F32Lt | F64Lt -> il.Append(il.Create(OpCodes.Clt))
        | I32LtU | I64LtU -> il.Append(il.Create(OpCodes.Clt_Un))

        | I32GtS | I64GtS | F32Gt | F64Gt -> il.Append(il.Create(OpCodes.Cgt))
        | I32GtU | I64GtU -> il.Append(il.Create(OpCodes.Cgt_Un))

        | F32Neg -> il.Append(il.Create(OpCodes.Neg))
        | F64Neg -> il.Append(il.Create(OpCodes.Neg))

        | I32Eq | I64Eq | F32Eq | F64Eq -> il.Append(il.Create(OpCodes.Ceq))

        | I32And | I64And -> il.Append(il.Create(OpCodes.And))
        | I32Or | I64Or -> il.Append(il.Create(OpCodes.Or))
        | I32Xor | I64Xor -> il.Append(il.Create(OpCodes.Xor))

        | I32Shl | I64Shl -> il.Append(il.Create(OpCodes.Shl))
        | I32ShrS | I64ShrS -> il.Append(il.Create(OpCodes.Shr))
        | I32ShrU | I64ShrU -> il.Append(il.Create(OpCodes.Shr_Un))
        | I32RemS | I64RemS -> il.Append(il.Create(OpCodes.Rem))
        | I32RemU | I64RemU -> il.Append(il.Create(OpCodes.Rem_Un))

        | F32ConvertI32S | F32ConvertI64S | F32DemoteF64 -> il.Append(il.Create(OpCodes.Conv_R4))
        | F64ConvertI32S | F64ConvertI64S | F64PromoteF32 -> il.Append(il.Create(OpCodes.Conv_R8))

        | F32ConvertI32U | F32ConvertI64U ->
            il.Append(il.Create(OpCodes.Conv_R_Un))
            il.Append(il.Create(OpCodes.Conv_R4))

        | F64ConvertI32U | F64ConvertI64U ->
            il.Append(il.Create(OpCodes.Conv_R_Un))
            il.Append(il.Create(OpCodes.Conv_R8))

        | I32WrapI64 -> il.Append(il.Create(OpCodes.Conv_I4)) // TODO is this correct?
        | I64ExtendI32S -> il.Append(il.Create(OpCodes.Conv_I8))
        | I64ExtendI32U -> il.Append(il.Create(OpCodes.Conv_I8)) // TODO is this correct?

        | I32TruncF32S | I32TruncF64S -> il.Append(il.Create(OpCodes.Conv_Ovf_I4))
        | I64TruncF32S | I64TruncF64S -> il.Append(il.Create(OpCodes.Conv_Ovf_I8))
        | I32TruncF32U | I32TruncF64U -> il.Append(il.Create(OpCodes.Conv_Ovf_I4_Un))
        | I64TruncF32U | I64TruncF64U -> il.Append(il.Create(OpCodes.Conv_Ovf_I8_Un))

        | I32Ne | I64Ne | F32Ne | F64Ne -> 
            il.Append(il.Create(OpCodes.Ceq))
            il.Append(il.Create(OpCodes.Ldc_I4_0))
            il.Append(il.Create(OpCodes.Ceq))

        | I32LeS | I64LeS ->
            il.Append(il.Create(OpCodes.Cgt))
            il.Append(il.Create(OpCodes.Ldc_I4_0))
            il.Append(il.Create(OpCodes.Ceq))

        | I32GeS | I64GeS ->
            il.Append(il.Create(OpCodes.Clt))
            il.Append(il.Create(OpCodes.Ldc_I4_0))
            il.Append(il.Create(OpCodes.Ceq))

        | I32LeU | I64LeU ->
            il.Append(il.Create(OpCodes.Cgt_Un))
            il.Append(il.Create(OpCodes.Ldc_I4_0))
            il.Append(il.Create(OpCodes.Ceq))

        | I32GeU | I64GeU ->
            il.Append(il.Create(OpCodes.Clt_Un))
            il.Append(il.Create(OpCodes.Ldc_I4_0))
            il.Append(il.Create(OpCodes.Ceq))

        | F32Le | F64Le ->
            il.Append(il.Create(OpCodes.Cgt_Un))
            il.Append(il.Create(OpCodes.Ldc_I4_0))
            il.Append(il.Create(OpCodes.Ceq))

        | F32Ge | F64Ge ->
            il.Append(il.Create(OpCodes.Clt_Un))
            il.Append(il.Create(OpCodes.Ldc_I4_0))
            il.Append(il.Create(OpCodes.Ceq))

        | Drop -> il.Append(il.Create(OpCodes.Pop))

        | Unreachable ->
            let ref_typ_e = ctx.md.ImportReference(typeof<System.Exception>.GetConstructor([| |]))
            il.Append(il.Create(OpCodes.Newobj, ref_typ_e))
            il.Append(il.Create(OpCodes.Throw))
            
        | Select -> 
            match result_type with
            | Some t ->
                let var_c = f_make_tmp ctx.bt.typ_i32
                il.Append(il.Create(OpCodes.Stloc, var_c))
                let var_v2 = f_make_tmp (cecil_valtype ctx.bt t)
                il.Append(il.Create(OpCodes.Stloc, var_v2))
                let var_v1 = f_make_tmp (cecil_valtype ctx.bt t)
                il.Append(il.Create(OpCodes.Stloc, var_v1))

                let push_v1 = il.Create(OpCodes.Ldloc, var_v1)
                let push_v2 = il.Create(OpCodes.Ldloc, var_v2)
                let lab_done = il.Create(OpCodes.Nop)

                il.Append(il.Create(OpCodes.Ldloc, var_c)) // put c back
                il.Append(il.Create(OpCodes.Brtrue, push_v1))
                il.Append(push_v2);
                il.Append(il.Create(OpCodes.Br, lab_done))
                il.Append(push_v1);
                il.Append(lab_done);
            | None -> failwith "should not happen"
        | MemorySize _ -> il.Append(il.Create(OpCodes.Ldsfld, ctx.mem_size))
        | MemoryGrow _ -> il.Append(il.Create(OpCodes.Call, ctx.mem_grow))
        | I32Clz ->
            gen_body_clz_i32 ctx.bt il f_make_tmp
        | I32Ctz ->
            gen_body_ctz_i32 ctx.bt il f_make_tmp
        | I32Popcnt ->
            gen_body_popcnt_i32 ctx.bt il f_make_tmp
        | I32Rotl ->
            // https://stackoverflow.com/questions/812022/c-sharp-bitwise-rotate-left-and-rotate-right
            // return (value << count) | (value >> (32 - count))

            let var_count = f_make_tmp (cecil_valtype ctx.bt I32)
            il.Append(il.Create(OpCodes.Stloc, var_count))
            let var_v = f_make_tmp (cecil_valtype ctx.bt I32)
            il.Append(il.Create(OpCodes.Stloc, var_v))

            il.Append(il.Create(OpCodes.Ldloc, var_v))
            il.Append(il.Create(OpCodes.Ldloc, var_count))
            il.Append(il.Create(OpCodes.Shl))

            il.Append(il.Create(OpCodes.Ldloc, var_v))
            il.Append(il.Create(OpCodes.Ldc_I4, 32))
            il.Append(il.Create(OpCodes.Ldloc, var_count))
            il.Append(il.Create(OpCodes.Sub))
            il.Append(il.Create(OpCodes.Shr_Un))

            il.Append(il.Create(OpCodes.Or))

        | I32Rotr ->
            // https://stackoverflow.com/questions/812022/c-sharp-bitwise-rotate-left-and-rotate-right
            // return (value >> count) | (value << (32 - count))

            let var_count = f_make_tmp (cecil_valtype ctx.bt I32)
            il.Append(il.Create(OpCodes.Stloc, var_count))
            let var_v = f_make_tmp (cecil_valtype ctx.bt I32)
            il.Append(il.Create(OpCodes.Stloc, var_v))

            il.Append(il.Create(OpCodes.Ldloc, var_v))
            il.Append(il.Create(OpCodes.Ldloc, var_count))
            il.Append(il.Create(OpCodes.Shr_Un))

            il.Append(il.Create(OpCodes.Ldloc, var_v))
            il.Append(il.Create(OpCodes.Ldc_I4, 32))
            il.Append(il.Create(OpCodes.Ldloc, var_count))
            il.Append(il.Create(OpCodes.Sub))
            il.Append(il.Create(OpCodes.Shl))

            il.Append(il.Create(OpCodes.Or))

        | I64Clz ->
            gen_body_clz_i64 ctx.bt il f_make_tmp
        | I64Ctz ->
            gen_body_ctz_i64 ctx.bt il f_make_tmp
        | I64Popcnt ->
            gen_body_popcnt_i64 ctx.bt il f_make_tmp
        | I64Rotl ->
            // https://stackoverflow.com/questions/812022/c-sharp-bitwise-rotate-left-and-rotate-right
            // return (value << count) | (value >> (64 - count))

            let var_count = f_make_tmp (cecil_valtype ctx.bt I32)
            il.Append(il.Create(OpCodes.Stloc, var_count))
            let var_v = f_make_tmp (cecil_valtype ctx.bt I64)
            il.Append(il.Create(OpCodes.Stloc, var_v))

            il.Append(il.Create(OpCodes.Ldloc, var_v))
            il.Append(il.Create(OpCodes.Ldloc, var_count))
            il.Append(il.Create(OpCodes.Shl))

            il.Append(il.Create(OpCodes.Ldloc, var_v))
            il.Append(il.Create(OpCodes.Ldc_I4, 64))
            il.Append(il.Create(OpCodes.Ldloc, var_count))
            il.Append(il.Create(OpCodes.Sub))
            il.Append(il.Create(OpCodes.Shr_Un))

            il.Append(il.Create(OpCodes.Or))

        | I64Rotr ->
            // https://stackoverflow.com/questions/812022/c-sharp-bitwise-rotate-left-and-rotate-right
            // return (value >> count) | (value << (64 - count))

            let var_count = f_make_tmp (cecil_valtype ctx.bt I32)
            il.Append(il.Create(OpCodes.Stloc, var_count))
            let var_v = f_make_tmp (cecil_valtype ctx.bt I64)
            il.Append(il.Create(OpCodes.Stloc, var_v))

            il.Append(il.Create(OpCodes.Ldloc, var_v))
            il.Append(il.Create(OpCodes.Ldloc, var_count))
            il.Append(il.Create(OpCodes.Shr_Un))

            il.Append(il.Create(OpCodes.Ldloc, var_v))
            il.Append(il.Create(OpCodes.Ldc_I4, 64))
            il.Append(il.Create(OpCodes.Ldloc, var_count))
            il.Append(il.Create(OpCodes.Sub))
            il.Append(il.Create(OpCodes.Shl))

            il.Append(il.Create(OpCodes.Or))

        | F32Copysign ->
            let v2 = f_make_tmp (cecil_valtype ctx.bt F32)
            il.Append(il.Create(OpCodes.Stloc, v2))
            let v1 = f_make_tmp (cecil_valtype ctx.bt F32)
            il.Append(il.Create(OpCodes.Stloc, v1))

            il.Append(il.Create(OpCodes.Ldloca, v1))
            il.Append(il.Create(OpCodes.Ldind_I4))
            il.Append(il.Create(OpCodes.Ldc_I4, 0x80000000))
            il.Append(il.Create(OpCodes.And))
            
            il.Append(il.Create(OpCodes.Ldloca, v2))
            il.Append(il.Create(OpCodes.Ldind_I4))
            il.Append(il.Create(OpCodes.Ldc_I4, 0x80000000))
            il.Append(il.Create(OpCodes.And))

            il.Append(il.Create(OpCodes.Xor))

            let lab_use_v1 = il.Create(OpCodes.Nop)
            let lab_done = il.Create(OpCodes.Nop)
            il.Append(il.Create(OpCodes.Brfalse, lab_use_v1))

            il.Append(il.Create(OpCodes.Ldloca, v1))
            il.Append(il.Create(OpCodes.Ldind_I4))
            il.Append(il.Create(OpCodes.Ldc_I4, 0x80000000))
            il.Append(il.Create(OpCodes.Xor))

            let v3 = f_make_tmp (cecil_valtype ctx.bt I32)
            il.Append(il.Create(OpCodes.Stloc, v3))

            il.Append(il.Create(OpCodes.Ldloca, v3))
            il.Append(il.Create(OpCodes.Ldind_R4))
            il.Append(il.Create(OpCodes.Br, lab_done))

            il.Append(lab_use_v1)
            il.Append(il.Create(OpCodes.Ldloc, v1))
            il.Append(lab_done);

            
        | F64Copysign ->
            let v2 = f_make_tmp (cecil_valtype ctx.bt F64)
            il.Append(il.Create(OpCodes.Stloc, v2))
            let v1 = f_make_tmp (cecil_valtype ctx.bt F64)
            il.Append(il.Create(OpCodes.Stloc, v1))

            il.Append(il.Create(OpCodes.Ldloca, v1))
            il.Append(il.Create(OpCodes.Ldind_I8))
            il.Append(il.Create(OpCodes.Ldc_I8, 0x8000000000000000L))
            il.Append(il.Create(OpCodes.And))
            
            il.Append(il.Create(OpCodes.Ldloca, v2))
            il.Append(il.Create(OpCodes.Ldind_I8))
            il.Append(il.Create(OpCodes.Ldc_I8, 0x8000000000000000L))
            il.Append(il.Create(OpCodes.And))

            il.Append(il.Create(OpCodes.Xor))

            let lab_use_v1 = il.Create(OpCodes.Nop)
            let lab_done = il.Create(OpCodes.Nop)
            il.Append(il.Create(OpCodes.Brfalse, lab_use_v1))

            il.Append(il.Create(OpCodes.Ldloca, v1))
            il.Append(il.Create(OpCodes.Ldind_I8))
            il.Append(il.Create(OpCodes.Ldc_I8, 0x8000000000000000L))
            il.Append(il.Create(OpCodes.Xor))

            let v3 = f_make_tmp (cecil_valtype ctx.bt I64)
            il.Append(il.Create(OpCodes.Stloc, v3))

            il.Append(il.Create(OpCodes.Ldloca, v3))
            il.Append(il.Create(OpCodes.Ldind_R8))
            il.Append(il.Create(OpCodes.Br, lab_done))

            il.Append(lab_use_v1)
            il.Append(il.Create(OpCodes.Ldloc, v1))
            il.Append(lab_done);

        | I32ReinterpretF32 ->
            let v = f_make_tmp (cecil_valtype ctx.bt F32)
            il.Append(il.Create(OpCodes.Stloc, v))
            il.Append(il.Create(OpCodes.Ldloca, v))
            il.Append(il.Create(OpCodes.Ldind_I4))
        | I64ReinterpretF64 ->
            let v = f_make_tmp (cecil_valtype ctx.bt F64)
            il.Append(il.Create(OpCodes.Stloc, v))
            il.Append(il.Create(OpCodes.Ldloca, v))
            il.Append(il.Create(OpCodes.Ldind_I8))
            
        | F32ReinterpretI32 ->
            let v = f_make_tmp (cecil_valtype ctx.bt I32)
            il.Append(il.Create(OpCodes.Stloc, v))
            il.Append(il.Create(OpCodes.Ldloca, v))
            il.Append(il.Create(OpCodes.Ldind_R4))
        | F64ReinterpretI64 ->
            let v = f_make_tmp (cecil_valtype ctx.bt I64)
            il.Append(il.Create(OpCodes.Stloc, v))
            il.Append(il.Create(OpCodes.Ldloca, v))
            il.Append(il.Create(OpCodes.Ldind_R8))

    let post_gen blocks result_type =
        match result_type with
        | Some t ->
            match try_peek blocks with
            | Some cur_block ->
                let bi = get_blockinfo cur_block
                let cur_opstack = bi.opstack
                push cur_opstack t
            | None -> ()
        | None -> ()

    let cecil_expr result (il: ILProcessor) ctx (f_make_tmp : TypeReference -> VariableDefinition) (a_locals : ParamOrVar[]) e =
        let body = 
            let opstack = new_stack_empty ()
            let lab_end = il.Create(OpCodes.Nop)
            { opstack = opstack; label = lab_end; result = result; stack_polymorphic = false; }

        let blocks = body |> CB_Body |> new_stack_one

        let dump s =
(*
            printfn "    %s" s
            printfn "        blocks : %A" (List.map (fun b -> get_block_string b) blocks.top)
            match try_peek blocks with
            | Some cur_block ->
                let bi = get_blockinfo cur_block
                let cur_opstack = bi.opstack
                printfn "        opstack: %A" cur_opstack
            | None -> ()
*)
            ()

        for op in e do

            //printfn "op: %A" op // (wasm.instr_name.get_instruction_name op)

            // TODO unconditional transfers put us in stack_polymorphic mode
            // until the end of the block.
            // br, br_table, return and unreachable
            // TOOD how to do deal with checking in stack_polymorphic mode?
            // skip type checking but still gen?  how to do the instructions
            // that need the result type, like callindirect and select?
            // just ignore everything?  when gen code that is unreachable?
            // but if the unreachable stuff contains more blocks, we need
            // to do proper nesting.
            // how does the result value of a block with an stack_polymorphic
            // tail happen?  in the case of Br 0, for example, it still 
            // needs to yield a value, right?  value needed to be one the
            // stack when the Br happened?

            dump "before"

            let in_stack_polymorphic_mode =
                let cur_block = peek blocks
                let cur_blockinfo = get_blockinfo cur_block
                cur_blockinfo.stack_polymorphic

            if in_stack_polymorphic_mode then
                // I am SO not interested in doing type checking
                // on unreachable code
                let result_type = gen_unreachable ctx blocks il op
                post_gen blocks result_type
            else
                let result_type = check_instr ctx a_locals blocks op
                gen_instr ctx a_locals blocks result_type body il f_make_tmp op
                post_gen blocks result_type

            dump "after"

    let create_global (gi : InternalGlobal) idx bt =
        let name = 
            match gi.name with
            | Some s -> s
            | None -> sprintf "global_%d" idx

        let typ = cecil_valtype bt gi.item.globaltype.typ

        let access = if gi.exported then FieldAttributes.Public else FieldAttributes.Private

        let method = 
            new FieldDefinition(
                name,
                access ||| Mono.Cecil.FieldAttributes.Static, 
                typ
                )

        method

    let create_method (fi : InternalFunc) fidx bt =
        let name = 
            match fi.name with
            | Some s -> s
            | None -> sprintf "func_%d" fidx

        let return_type =
            match function_result_type fi.typ with
            | Some t -> cecil_valtype bt t
            | None -> bt.typ_void

        let access = if fi.exported then MethodAttributes.Public else MethodAttributes.Private

        let method = 
            new MethodDefinition(
                name,
                access ||| Mono.Cecil.MethodAttributes.Static, 
                return_type
                )

        method

    let gen_function_code ctx (mi : MethodRefInternal) =
        let a_locals =
            let a = System.Collections.Generic.List<ParamOrVar>()
            // TODO look up names in mi.func.locals
            let get_name () =
                sprintf "p%d" (a.Count)
            for x in mi.func.typ.parms do
                let typ = cecil_valtype ctx.bt x
                let name = get_name()
                let def = new ParameterDefinition(name, ParameterAttributes.None, typ)
                a.Add(ParamRef { def_param = def; typ = x; })
            for loc in mi.func.code.locals do
                // TODO assert count > 0 ?
                for x = 1 to (int loc.count) do
                    let typ = cecil_valtype ctx.bt loc.localtype
                    let def = new VariableDefinition(typ)
                    a.Add(LocalRef { def_var = def; typ = loc.localtype })
            Array.ofSeq a
            
        mi.method.Body.InitLocals <- true
        for pair in a_locals do
            match pair with
            | ParamRef { def_param = def } -> mi.method.Parameters.Add(def)
            | LocalRef { def_var = def } -> mi.method.Body.Variables.Add(def)

        let f_make_tmp = make_tmp mi.method

        let il = mi.method.Body.GetILProcessor()

        match ctx.profile with
        | Some h ->
            il.Append(il.Create(OpCodes.Ldstr, mi.method.Name))
            il.Append(il.Create(OpCodes.Call, h.profile_enter))
        | None -> ()

        match ctx.trace with
        | Some h ->
            let v_parms = new VariableDefinition(ctx.md.ImportReference(typeof<System.Object[]>))
            mi.method.Body.Variables.Add(v_parms)
            let parms =
                a_locals
                |> Array.choose (fun x -> match x with | ParamRef x -> Some (x) | LocalRef _ -> None)
            il.Append(il.Create(OpCodes.Ldc_I4, parms.Length))
            il.Append(il.Create(OpCodes.Newarr, ctx.bt.typ_object))
            il.Append(il.Create(OpCodes.Stloc, v_parms))
            let f_parm (i : int32) p =
                il.Append(il.Create(OpCodes.Ldloc, v_parms))
                il.Append(il.Create(OpCodes.Ldc_I4, i))
                let { def_param = def; typ = t } = p
                let typ = cecil_valtype ctx.bt t
                // TODO it would be nice to include names here?
                il.Append(il.Create(OpCodes.Ldarg, def))
                il.Append(il.Create(OpCodes.Box, typ))
                il.Append(il.Create(OpCodes.Stelem_Ref))
            Array.iteri f_parm parms
            il.Append(il.Create(OpCodes.Ldstr, mi.method.Name))
            il.Append(il.Create(OpCodes.Ldloc, v_parms))
            il.Append(il.Create(OpCodes.Call, h.trace_enter))
        | None -> ()

        let result_typ = function_result_type mi.func.typ
        cecil_expr result_typ il ctx f_make_tmp a_locals mi.func.code.expr
        match ctx.profile with
        | Some h ->
            il.Append(il.Create(OpCodes.Ldstr, mi.method.Name))
            il.Append(il.Create(OpCodes.Call, h.profile_exit))
        | None -> ()

        match ctx.trace with
        | Some h ->
            match result_typ with
            | Some t ->
                let typ =cecil_valtype ctx.bt t
                il.Append(il.Create(OpCodes.Dup))
                il.Append(il.Create(OpCodes.Box, typ))
                il.Append(il.Create(OpCodes.Ldstr, mi.method.Name))
                il.Append(il.Create(OpCodes.Call, h.trace_exit_value))
            | None ->
                il.Append(il.Create(OpCodes.Ldstr, mi.method.Name))
                il.Append(il.Create(OpCodes.Call, h.trace_exit_void))
        | None -> ()

        il.Append(il.Create(OpCodes.Ret))

    let create_methods ndx bt (md : ModuleDefinition) env_assembly =
        let prep_func i fi =
            match fi with
            | ImportedFunc s ->
                match env_assembly with
                | Some assy ->
                    let method = import_function md s assy
                    MethodRefImported { MethodRefImported.func = s; method = method }
                | None -> failwith "no imports"
            | InternalFunc q ->
                let method = create_method q i bt
                MethodRefInternal { func = q; method = method; }

        let a_methods = Array.mapi prep_func ndx.FuncLookup
        a_methods

    let gen_code_for_methods ctx =
        for m in ctx.a_methods do
            match m with
            | MethodRefInternal mi -> gen_function_code ctx mi
            | MethodRefImported _ -> ()

    let create_globals ndx bt (md : ModuleDefinition) env_assembly =
        let prep i gi =
            match gi with
            | ImportedGlobal s ->
                match env_assembly with
                | Some assy ->
                    let field = import_global md s assy
                    GlobalRefImported { GlobalRefImported.glob = s; field = field; }
                | None -> failwith "no imports"
            | InternalGlobal q ->
                let field = create_global q i bt
                GlobalRefInternal { glob = q; field = field; }

        let a_globals = Array.mapi prep ndx.GlobalLookup

        a_globals

    let gen_tbl_lookup ndx bt (tbl : FieldDefinition) (main_mod : ModuleDefinition) =
        let method = 
            new MethodDefinition(
                "__tbl_lookup",
                MethodAttributes.Public ||| MethodAttributes.Static,
                bt.typ_intptr
                )
        let parm = new ParameterDefinition(bt.typ_i32)
        method.Parameters.Add(parm)

        // TODO this needs to do type check, range check

        let il = method.Body.GetILProcessor()

        il.Append(il.Create(OpCodes.Ldsfld, tbl))
        il.Append(il.Create(OpCodes.Ldarg, parm))
        il.Append(il.Create(OpCodes.Ldc_I4, 8))
        il.Append(il.Create(OpCodes.Mul))
        il.Append(il.Create(OpCodes.Add))
        il.Append(il.Create(OpCodes.Ldind_I))

        let lab = il.Create(OpCodes.Nop)
        il.Append(il.Create(OpCodes.Dup))
        il.Append(il.Create(OpCodes.Brtrue, lab))

        let ref_typ_e = main_mod.ImportReference(typeof<System.Exception>.GetConstructor([| |]))
        il.Append(il.Create(OpCodes.Newobj, ref_typ_e))
        il.Append(il.Create(OpCodes.Throw))

        il.Append(lab)
        il.Append(il.Create(OpCodes.Ret))

        method

    let gen_data_setup ndx ctx (ref_getresource : MethodReference) (a_datas : DataStuff[]) =
        let method = 
            new MethodDefinition(
                "__data_setup",
                MethodAttributes.Public ||| MethodAttributes.Static,
                ctx.bt.typ_void
                )
        let il = method.Body.GetILProcessor()

        // need to grab a reference to this assembly 
        // (the one that contains our resources)
        // and store it in a local so we can use it
        // each time through the loop.

        let ref_typ_assembly = ctx.md.ImportReference(typeof<System.Reflection.Assembly>)
        let loc_assembly = new VariableDefinition(ref_typ_assembly)
        method.Body.Variables.Add(loc_assembly)
        let ref_gea = ctx.md.ImportReference(typeof<System.Reflection.Assembly>.GetMethod("GetExecutingAssembly"))
        il.Append(il.Create(OpCodes.Call, ref_gea))
        il.Append(il.Create(OpCodes.Stloc, loc_assembly))

        // also need to import Marshal.Copy

        let ref_mcopy = ctx.md.ImportReference(typeof<System.Runtime.InteropServices.Marshal>.GetMethod("Copy", [| typeof<byte[]>; typeof<int32>; typeof<nativeint>; typeof<int32> |]))

        for d in a_datas do
            // the 4 args to Marshal.Copy...

            // the byte array containing the resource
            il.Append(il.Create(OpCodes.Ldloc, loc_assembly))
            il.Append(il.Create(OpCodes.Ldstr, d.name))
            il.Append(il.Create(OpCodes.Call, ref_getresource))

            // 0, the offset
            il.Append(il.Create(OpCodes.Ldc_I4, 0))

            // the destination pointer
            il.Append(il.Create(OpCodes.Ldsfld, ctx.mem))
            let f_make_tmp = make_tmp method
            cecil_expr (Some I32) il ctx f_make_tmp Array.empty d.item.offset
            il.Append(il.Create(OpCodes.Add))

            // and the length
            il.Append(il.Create(OpCodes.Ldc_I4, d.item.init.Length))

            // now copy
            il.Append(il.Create(OpCodes.Call, ref_mcopy))

        il.Append(il.Create(OpCodes.Ret))

        method

    let gen_tbl_setup ndx ctx (tbl : FieldDefinition) lim (elems : ElementItem[]) =
        let method = 
            new MethodDefinition(
                "__tbl_setup",
                MethodAttributes.Public ||| MethodAttributes.Static,
                ctx.bt.typ_void
                )
        let il = method.Body.GetILProcessor()

        let count_tbl_entries = 
            match lim with
            | Min m -> m
            | MinMax (min,max) -> max
        let size_in_bytes = (int count_tbl_entries) * 8 // TODO nativeint size 4 vs 8 

        il.Append(il.Create(OpCodes.Ldc_I4, size_in_bytes))
        // TODO where is this freed?
        // TODO the docs say the param to AllocHGlobal is an IntPtr
        let ext = ctx.mem.Module.ImportReference(typeof<System.Runtime.InteropServices.Marshal>.GetMethod("AllocHGlobal", [| typeof<int32> |] ))
        il.Append(il.Create(OpCodes.Call, ext))
        il.Append(il.Create(OpCodes.Stsfld, tbl))

        // memset 0
        il.Append(il.Create(OpCodes.Ldsfld, tbl))
        il.Append(il.Create(OpCodes.Ldc_I4, 0))
        il.Append(il.Create(OpCodes.Ldc_I4, size_in_bytes))
        il.Append(il.Create(OpCodes.Initblk))

        let f_make_tmp = make_tmp method

        let tmp = VariableDefinition(ctx.bt.typ_i32)
        method.Body.Variables.Add(tmp)

        for elem in elems do
            cecil_expr (Some I32) il ctx f_make_tmp Array.empty elem.offset

            il.Append(il.Create(OpCodes.Stloc, tmp))
            for i = 0 to (elem.init.Length - 1) do

                // prep the addr
                il.Append(il.Create(OpCodes.Ldsfld, tbl))
                il.Append(il.Create(OpCodes.Ldloc, tmp))
                il.Append(il.Create(OpCodes.Ldc_I4, i))
                il.Append(il.Create(OpCodes.Add))
                il.Append(il.Create(OpCodes.Ldc_I4, 8))
                il.Append(il.Create(OpCodes.Mul))
                il.Append(il.Create(OpCodes.Add))

                // now the func ptr
                let (FuncIdx fidx) = elem.init.[i]
                let m = ctx.a_methods.[int fidx]
                let m = 
                    match m with
                    | MethodRefImported m -> m.method
                    | MethodRefInternal m -> m.method :> MethodReference
                il.Append(il.Create(OpCodes.Ldftn, m))

                // and store it
                il.Append(il.Create(OpCodes.Stind_I))

        il.Append(il.Create(OpCodes.Ret))

        method

    let gen_cctor ctx mem_size_in_pages (tbl_setup : MethodDefinition option) (data_setup : MethodDefinition option) =
        let method = 
            new MethodDefinition(
                ".cctor",
                MethodAttributes.Private ||| MethodAttributes.Static ||| MethodAttributes.SpecialName ||| MethodAttributes.RTSpecialName,
                ctx.bt.typ_void
                )
        let il = method.Body.GetILProcessor()

        let size_in_bytes = mem_size_in_pages * mem_page_size

        il.Append(il.Create(OpCodes.Ldc_I4, mem_size_in_pages))
        il.Append(il.Create(OpCodes.Stsfld, ctx.mem_size))

        il.Append(il.Create(OpCodes.Ldc_I4, size_in_bytes))
        // TODO where is this freed?
        // TODO the docs say the param to AllocHGlobal is an IntPtr
        let ext = ctx.mem.Module.ImportReference(typeof<System.Runtime.InteropServices.Marshal>.GetMethod("AllocHGlobal", [| typeof<int32> |] ))
        il.Append(il.Create(OpCodes.Call, ext))
        il.Append(il.Create(OpCodes.Stsfld, ctx.mem))

        // memset 0
        il.Append(il.Create(OpCodes.Ldsfld, ctx.mem))
        il.Append(il.Create(OpCodes.Ldc_I4, 0))
        il.Append(il.Create(OpCodes.Ldc_I4, size_in_bytes))
        il.Append(il.Create(OpCodes.Initblk))

        match tbl_setup with
        | Some m -> il.Append(il.Create(OpCodes.Call, m))
        | None -> ()

        match data_setup with
        | Some m -> il.Append(il.Create(OpCodes.Call, m))
        | None -> ()

        let f_make_tmp = make_tmp method

        // TODO mv globals out into a different func?
        for g in ctx.a_globals do
            match g with
            | GlobalRefInternal gi -> 
                cecil_expr (Some gi.glob.item.globaltype.typ) il ctx f_make_tmp Array.empty gi.glob.item.init
                il.Append(il.Create(OpCodes.Stsfld, gi.field))
            | GlobalRefImported _ -> ()

        il.Append(il.Create(OpCodes.Ret))

        method

(*

    public static byte[] GetResource(System.Reflection.Assembly a, string name)
    {
        using (var strm = a.GetManifestResourceStream(name))
        {
            var ms = new System.IO.MemoryStream();
            strm.CopyTo(ms);
            return ms.ToArray();
        }
    }

  .method public hidebysig static uint8[]
          GetResource(class [netstandard]System.Reflection.Assembly a,
                      string name) cil managed
  {
    // Code size       46 (0x2e)
    .maxstack  2
    .locals init (class [netstandard]System.IO.Stream V_0,
             class [netstandard]System.IO.MemoryStream V_1,
             uint8[] V_2)
    IL_0000:  nop
    IL_0001:  ldarg.0
    IL_0002:  ldarg.1
    IL_0003:  callvirt   instance class [netstandard]System.IO.Stream [netstandard]System.Reflection.Assembly::GetManifestResourceStream(string)
    IL_0008:  stloc.0
    .try
    {
      IL_0009:  nop
      IL_000a:  newobj     instance void [netstandard]System.IO.MemoryStream::.ctor()
      IL_000f:  stloc.1
      IL_0010:  ldloc.0
      IL_0011:  ldloc.1
      IL_0012:  callvirt   instance void [netstandard]System.IO.Stream::CopyTo(class [netstandard]System.IO.Stream)
      IL_0017:  nop
      IL_0018:  ldloc.1
      IL_0019:  callvirt   instance uint8[] [netstandard]System.IO.MemoryStream::ToArray()
      IL_001e:  stloc.2
      IL_001f:  leave.s    IL_002c

    }  // end .try
    finally
    {
      IL_0021:  ldloc.0
      IL_0022:  brfalse.s  IL_002b

      IL_0024:  ldloc.0
      IL_0025:  callvirt   instance void [netstandard]System.IDisposable::Dispose()
      IL_002a:  nop
      IL_002b:  endfinally
    }  // end handler
    IL_002c:  ldloc.2
    IL_002d:  ret
  } // end of method env::GetResource

*)

    let gen_load_resource ctx (main_mod : ModuleDefinition) =
        let method = 
            new MethodDefinition(
                "__load_resource",
                MethodAttributes.Private ||| MethodAttributes.Static,
                main_mod.ImportReference(typeof<byte[]>)
                )

        let parm_a = new ParameterDefinition(main_mod.ImportReference(typeof<System.Reflection.Assembly>))
        method.Parameters.Add(parm_a)

        let parm_name = new ParameterDefinition(main_mod.ImportReference(typeof<string>))
        method.Parameters.Add(parm_name)

        let v_strm = new VariableDefinition(main_mod.ImportReference(typeof<System.IO.Stream>))
        method.Body.Variables.Add(v_strm)

        let v_ms = new VariableDefinition(main_mod.ImportReference(typeof<System.IO.MemoryStream>))
        method.Body.Variables.Add(v_ms)

        let v_result = new VariableDefinition(main_mod.ImportReference(typeof<byte[]>))
        method.Body.Variables.Add(v_result)

        let il = method.Body.GetILProcessor()

        il.Append(il.Create(OpCodes.Ldarg, parm_a))
        il.Append(il.Create(OpCodes.Ldarg, parm_name))
        il.Append(il.Create(OpCodes.Callvirt, main_mod.ImportReference(typeof<System.Reflection.Assembly>.GetMethod("GetManifestResourceStream", [| typeof<string> |]))))
        il.Append(il.Create(OpCodes.Stloc, v_strm))

        il.Append(il.Create(OpCodes.Newobj, main_mod.ImportReference(typeof<System.IO.MemoryStream>.GetConstructor([| |]))))
        il.Append(il.Create(OpCodes.Stloc, v_ms))

        il.Append(il.Create(OpCodes.Ldloc, v_strm))
        il.Append(il.Create(OpCodes.Ldloc, v_ms))
        il.Append(il.Create(OpCodes.Callvirt, main_mod.ImportReference(typeof<System.IO.Stream>.GetMethod("CopyTo", [| typeof<System.IO.Stream> |]))))

        il.Append(il.Create(OpCodes.Ldloc, v_ms))
        il.Append(il.Create(OpCodes.Callvirt, main_mod.ImportReference(typeof<System.IO.MemoryStream>.GetMethod("ToArray", [| |]))))
        il.Append(il.Create(OpCodes.Stloc, v_result))

        il.Append(il.Create(OpCodes.Ldloc, v_strm))
        il.Append(il.Create(OpCodes.Callvirt, main_mod.ImportReference(typeof<System.IDisposable>.GetMethod("Dispose", [| |]))))

(* TODO
        let handler = ExceptionHandler(ExceptionHandlerType.Finally)
        handler.TryStart <- begin_try
        handler.TryEnd <- end_try
        handler.HandlerStart <- begin_finally
        handler.HandlerEnd <- end_finally
        handler.CatchType <- main_mod.ImportReference(typeof<System.Exception>)
        method.Body.ExceptionHandlers.Add(handler)
*)

        il.Append(il.Create(OpCodes.Ldloc, v_result))

        il.Append(il.Create(OpCodes.Ret))

        method

    let gen_grow_mem (mem : FieldReference) (mem_size : FieldReference) bt trace profile =
        let method = 
            new MethodDefinition(
                "__grow_mem",
                MethodAttributes.Private ||| MethodAttributes.Static,
                bt.typ_i32
                )
        let parm = new ParameterDefinition(bt.typ_i32)
        method.Parameters.Add(parm)

        let v_old_size = new VariableDefinition(bt.typ_i32)
        method.Body.Variables.Add(v_old_size)

        let v_new_size = new VariableDefinition(bt.typ_i32)
        method.Body.Variables.Add(v_new_size)

        let v_old_mem = new VariableDefinition(bt.typ_intptr)
        method.Body.Variables.Add(v_old_mem)

        let v_new_mem = new VariableDefinition(bt.typ_intptr)
        method.Body.Variables.Add(v_new_mem)

        let il = method.Body.GetILProcessor()

        match profile with
        | Some h ->
            il.Append(il.Create(OpCodes.Ldstr, method.Name))
            il.Append(il.Create(OpCodes.Call, h.profile_enter))
        | None -> ()

        il.Append(il.Create(OpCodes.Ldsfld, mem))
        il.Append(il.Create(OpCodes.Stloc, v_old_mem))

        il.Append(il.Create(OpCodes.Ldsfld, mem_size))
        il.Append(il.Create(OpCodes.Stloc, v_old_size))

        il.Append(il.Create(OpCodes.Ldloc, v_old_size))
        il.Append(il.Create(OpCodes.Ldarg, parm))
        il.Append(il.Create(OpCodes.Add))
        il.Append(il.Create(OpCodes.Stloc, v_new_size))

        il.Append(il.Create(OpCodes.Ldloc, v_old_mem))
        il.Append(il.Create(OpCodes.Ldloc, v_new_size))
        il.Append(il.Create(OpCodes.Ldc_I4, mem_page_size))
        il.Append(il.Create(OpCodes.Mul))

        let method_realloc = typeof<System.Runtime.InteropServices.Marshal>.GetMethod("ReAllocHGlobal", [| typeof<nativeint>; typeof<nativeint> |] )
        if method_realloc = null then
            failwith "ReAllocHGlobal not found"

        // TODO this should do try/catch and return -1 if it fails

        // TODO where is this freed?
        let ext = mem.Module.ImportReference(method_realloc)
        il.Append(il.Create(OpCodes.Call, ext))
        il.Append(il.Create(OpCodes.Stloc, v_new_mem))

        match trace with
        | Some h ->
            il.Append(il.Create(OpCodes.Ldloc, v_old_size))
            il.Append(il.Create(OpCodes.Ldloc, v_old_mem))
            il.Append(il.Create(OpCodes.Ldarg, parm))
            il.Append(il.Create(OpCodes.Ldloc, v_new_size))
            il.Append(il.Create(OpCodes.Ldloc, v_new_mem))
            il.Append(il.Create(OpCodes.Call, h.trace_grow_mem))
        | None -> ()

        il.Append(il.Create(OpCodes.Ldloc, v_new_size))
        il.Append(il.Create(OpCodes.Stsfld, mem_size))
        il.Append(il.Create(OpCodes.Ldloc, v_new_mem))
        il.Append(il.Create(OpCodes.Stsfld, mem))

        // memset 0

        il.Append(il.Create(OpCodes.Ldsfld, mem))
        il.Append(il.Create(OpCodes.Ldloc, v_old_size))
        il.Append(il.Create(OpCodes.Ldc_I4, mem_page_size))
        il.Append(il.Create(OpCodes.Mul))
        il.Append(il.Create(OpCodes.Add))

        il.Append(il.Create(OpCodes.Ldc_I4, 0))

        il.Append(il.Create(OpCodes.Ldarg, parm))
        il.Append(il.Create(OpCodes.Ldc_I4, mem_page_size))
        il.Append(il.Create(OpCodes.Mul))

        il.Append(il.Create(OpCodes.Initblk))

        // return value

        il.Append(il.Create(OpCodes.Ldloc, v_old_size))

        match profile with
        | Some h ->
            il.Append(il.Create(OpCodes.Ldstr, method.Name))
            il.Append(il.Create(OpCodes.Call, h.profile_exit))
        | None -> ()

        il.Append(il.Create(OpCodes.Ret))

        method

    let create_data_resources ctx sd =
        let f i d =
            let name = sprintf "data_%d" i
            // public so tests can load the resource manually
            let flags = ManifestResourceAttributes.Public
            let r = EmbeddedResource(name, flags, d.init)
            { item = d; name = name; resource = r; }
        Array.mapi f sd.datas

    let gen_assembly settings m assembly_name ns classname (ver : System.Version) (dest : System.IO.Stream) =
        let assembly = 
            AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition(
                    assembly_name, 
                    ver
                    ), 
                assembly_name, 
                ModuleKind.Dll
                )

        let main_module = assembly.MainModule

        let bt = 
            {
                typ_i32 = main_module.TypeSystem.Int32
                typ_i64 = main_module.TypeSystem.Int64
                typ_f32 = main_module.TypeSystem.Single
                typ_f64 = main_module.TypeSystem.Double
                typ_void = main_module.TypeSystem.Void
                typ_intptr = main_module.TypeSystem.IntPtr
                typ_object = main_module.TypeSystem.Object
            }

        let container = 
            new TypeDefinition(
                ns,
                classname,
                TypeAttributes.Class ||| TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed, 
                main_module.TypeSystem.Object
                )

        main_module.Types.Add(container);

        let ndx = get_module_index m

        let (mem, mem_size) =
            match settings.memory with
            | AlwaysImportPairFrom name ->
                // when targeting wasi, we ignore what the module says about
                // memory and import the one from our wasi implementation.

                match settings.env with
                | Some assembly ->
                    let mem_size = import_field main_module name "__mem_size" assembly
                    let mem = import_field main_module name "__mem" assembly
                    (mem, mem_size)
                | None ->
                    failwith "no reference assembly"
            | Default->
                // in this case, we accept an optional assembly for looking
                // up imports, and we respect whatever the module says about
                // memory.  if there is no import assembly but the module
                // tries to import a memory, it will fail.

                let mem_size =
                    let f =
                        new FieldDefinition(
                            "__mem_size",
                            FieldAttributes.Public ||| FieldAttributes.Static, 
                            main_module.TypeSystem.Int32
                            )
                    container.Fields.Add(f)
                    f :> FieldReference

                let mem =
                    match ndx.MemoryImport with
                    | Some mi ->
                        match settings.env with
                        | Some assembly -> import_memory main_module mi assembly
                        | None -> failwith "no imports"
                    | None ->
                        let f =
                            new FieldDefinition(
                                "__mem",
                                FieldAttributes.Public ||| FieldAttributes.Static, 
                                main_module.TypeSystem.IntPtr
                                )
                        container.Fields.Add(f)
                        f :> FieldReference
                (mem, mem_size)

        let a_globals = create_globals ndx bt main_module settings.env

        for m in a_globals do
            match m with
            | GlobalRefInternal mi -> container.Fields.Add(mi.field)
            | GlobalRefImported _ -> ()

        let a_methods = create_methods ndx bt main_module settings.env

        for m in a_methods do
            match m with
            | MethodRefInternal mi -> container.Methods.Add(mi.method)
            | MethodRefImported mi -> ()

        let tbl =
            new FieldDefinition(
                "__tbl",
                FieldAttributes.Public ||| FieldAttributes.Static, 
                main_module.TypeSystem.IntPtr
                )
        container.Fields.Add(tbl)

        let find_method (md : ModuleDefinition) (a : System.Reflection.Assembly) (type_name : string) (method_name : string) (parms : System.Type[]) =
            let t = a.GetType(type_name)
            if t <> null then
                let m = t.GetMethod(method_name, parms)
                if m <> null then
                    md.ImportReference(m)
                else
                    null
            else
                null

        let tbl_lookup = 
            let has_table =
                match (ndx.Table, ndx.TableImport, ndx.Element) with
                | (Some _, None, Some _) -> true
                | (None, Some _, Some _) -> true
                | _ -> false
            if has_table then
                let m = gen_tbl_lookup ndx bt tbl main_module
                container.Methods.Add(m)
                Some m
            else None

        let types =
            match ndx.Type with
            | Some s -> s.types
            | None -> [| |]

        let trace_hooks =
            match settings.trace with
            | TraceSetting.No -> None 
            | TraceSetting.Yes a ->
                Some {
                    trace_enter = find_method container.Module a "__trace" "Enter" [| typeof<string>; typeof<System.Object[]> |]
                    trace_exit_value = find_method container.Module a "__trace" "Exit" [| typeof<string>; typeof<System.Object> |]
                    trace_exit_void = find_method container.Module a "__trace" "Exit" [| typeof<string>; |]
                    trace_grow_mem = find_method container.Module a "__trace" "GrowMem" [| typeof<int32>; typeof<int32>; typeof<int32>; typeof<int32>; typeof<int32>; |]
                    }

        let profile_hooks =
            match settings.profile with
            | ProfileSetting.No -> None 
            | ProfileSetting.Yes a ->
                Some {
                    profile_enter = find_method container.Module a "__profile" "Enter" [| typeof<string> |]
                    profile_exit = find_method container.Module a "__profile" "Exit" [| typeof<string> |]
                    }

        let mem_grow =
            gen_grow_mem mem mem_size bt trace_hooks profile_hooks
        container.Methods.Add(mem_grow)

        let ctx =
            {
                types = types
                md = container.Module
                bt = bt
                a_globals = a_globals
                a_methods = a_methods
                mem = mem
                mem_size = mem_size
                mem_grow = mem_grow
                tbl_lookup = tbl_lookup
                trace = trace_hooks
                profile = profile_hooks
            }

        let tbl_setup =
            match (ndx.Table, ndx.TableImport, ndx.Element) with
            | (Some st, None, Some se) ->
                let lim = st.tables.[0].limits
                let m = gen_tbl_setup ndx ctx tbl lim se.elems
                container.Methods.Add(m)
                Some m
            | (None, Some st, Some se) ->
                // TODO for now, just ignore the fact that this tbl is imported
                let lim = st.tbl.limits
                let m = gen_tbl_setup ndx ctx tbl lim se.elems
                container.Methods.Add(m)
                Some m
            | (Some st, None, None) ->
                // module declares a table but not elements
                None
            | (None, None, None) ->
                None

        gen_code_for_methods ctx

        let data_setup =
            match ndx.Data with
            | Some sd -> 
                let a_datas = create_data_resources ctx sd
                let load_data = gen_load_resource ctx main_module
                container.Methods.Add(load_data)
                for d in a_datas do
                    main_module.Resources.Add(d.resource)
                let m = gen_data_setup ndx ctx load_data a_datas
                container.Methods.Add(m)
                Some m
            | None -> None

        let mem_size_in_pages =
            match ndx.Memory with
            | Some { mems = [| { limits = lim } |] } -> 
                match lim with
                | Min m -> m
                | MinMax (min,max) -> min
            | None -> 1u
            | _ -> 1u
        let cctor = gen_cctor ctx (int mem_size_in_pages) tbl_setup data_setup
        container.Methods.Add(cctor)

        assembly.Write(dest);

