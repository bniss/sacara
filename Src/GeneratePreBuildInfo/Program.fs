﻿/// This simple program produce a text output with VM opcodes. it must be used each time 
// that a new instruction is added to the VM.
/// The generated opcode are saved in the appropriate src directory.
namespace GeneratePreBuildInfo

open System
open System.Collections.Generic
open System.Text
open Microsoft.FSharp.Reflection
open Newtonsoft.Json
open System.IO
open System.Reflection
open ES.Sacara.Ir.Core
open ES.Sacara.Ir.Core.Instructions

module Program =
    let private writeFile(filename: String, content: String) =
        File.WriteAllText(filename, content)

    let getSavedOpcodes() =
        let curDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
        let assemblerSrcFile = Path.Combine(curDir, "..", "..", "..", "ES.Sacara.Ir.Core", "vm_opcodes.json")
        let fileContent = File.ReadAllText(assemblerSrcFile)
        
        JsonConvert.DeserializeObject<List<VmOpCodeItem>>(fileContent)
        |> Seq.map(fun opCode -> (opCode.Name, opCode))
        |> dict

    let encryptOpCode(opCode: VmOpCodeItem) =
        opCode.OpCodes
        |> Seq.iteri(fun i opCodeValue ->
            opCode.Bytes.Add(BitConverter.GetBytes(uint16((opCodeValue ^^^ 0xB5) + opCode.OpCodes.Count)))
        )
        opCode

    let generateOpCodes() =
        let opCodes = getSavedOpcodes()
        let opCodesBytes = new HashSet<Int32>(opCodes.Values |> Seq.collect(fun opCode -> opCode.OpCodes))
        let rnd = new Random()

        FSharpType.GetUnionCases(typeof<VmInstruction>)
        |> Array.map(fun case ->   
            if opCodes.Keys |> Seq.contains case.Name then
                opCodes.[case.Name]
            else
                let numberOfCases = rnd.Next(2, 6)
                let mutable iteration = 0
                let newOpCode =  new VmOpCodeItem(Name = case.Name)

                while iteration < numberOfCases do
                    // clear initial 4 bits since they are flags
                    let opCodeBytes = rnd.Next(10, 65534) &&& 0x0FFF

                    if opCodesBytes.Add(opCodeBytes) then    
                        newOpCode.OpCodes.Add(opCodeBytes)
                        iteration <- iteration + 1

                encryptOpCode(newOpCode)
        )
        |> Array.map(fun opCode -> (opCode.Name, opCode))
        |> dict

    let saveOpCodeInAssemblerDir(opCodes: IDictionary<String, VmOpCodeItem>) =
        let opCodeJson = JsonConvert.SerializeObject(opCodes.Values, Formatting.Indented)
        
        // copy file
        let curDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
        let assemblerSrcFile = Path.Combine(curDir, "..", "..", "..", "ES.Sacara.Ir.Core", "vm_opcodes.json")
        writeFile(assemblerSrcFile, opCodeJson)
        Console.WriteLine("Files copied to: " + assemblerSrcFile)

    let private convertBytesToDword(wordBytes: Byte array) =
        let wordString = String.Join(String.Empty, wordBytes |> Seq.rev |> Seq.map(fun b -> b.ToString("X").PadLeft(2, '0')))
        String.Format("0{0}h", wordString)

    let private convertToDword(word32: Int32) =
        let word16 = UInt16.Parse(word32.ToString())
        let wordBytes = BitConverter.GetBytes(word16)
        convertBytesToDword(wordBytes)    

    let saveOpCodeInVmDir(opCodes: IDictionary<String, VmOpCodeItem>) =
        let sb = new StringBuilder()
        sb.AppendLine("; This file is auto generated, don't modify it") |> ignore

        let rnd = new Random()
        let generateMarker() = uint32(rnd.Next(0, 0xFFFF) <<< 18 ||| rnd.Next(0, 0xFFFF)).ToString("X").PadLeft(8, '0')
        let marker1 = generateMarker()
        let marker2 = generateMarker()
        sb.AppendFormat("marker1 EQU 0{0}h", marker1).AppendLine() |> ignore
        sb.AppendFormat("marker2 EQU 0{0}h", marker2).AppendLine().AppendLine() |> ignore
        
        opCodes
        |> Seq.map(fun kv -> kv.Value)
        |> Seq.iter(fun opCode ->
            let obfuscatedBytes = String.Join(", ", opCode.Bytes |> Seq.map convertBytesToDword)
            let realBytes = String.Join(", ", opCode.OpCodes |> Seq.map convertToDword)

            sb.AppendFormat(
                "; real opcodes: {1}{0}header_{2} EQU <DWORD marker1, marker2, {3}h, {4}>{0}", 
                Environment.NewLine,
                realBytes,
                opCode.Name,
                opCode.Bytes.Count, 
                obfuscatedBytes              
              
            ).AppendLine() |> ignore
        )

        // write end marker
        sb.Append("header_marker EQU <DWORD marker2, marker1>").AppendLine() |> ignore
        
        // copy file
        let fileContent = sb.ToString()
        let curDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
        let vmSrcFile = Path.Combine(curDir, "..", "..", "..", "SacaraVm", "instructions_headers.inc")
        writeFile(vmSrcFile, fileContent)
        Console.WriteLine("Files copied to: " + vmSrcFile)

    let private ror(operand: UInt32) =
        let n = 6
        (operand >>> n) ||| (operand <<< (32-n))

    let private rol(operand: UInt32) =
        let n = 7
        (operand <<< n) ||| (operand >>> (32-n))

    let private hashString(name: String) =
        let mutable hash = uint32 0
        name.ToUpperInvariant()
        |> Seq.iteri(fun i c ->
            let h1 = (hash + uint32 c) * uint32 1024
            let h2 = ror h1
            let h3 = (h1 ^^^ h2) ^^^ (uint32 i ^^^ uint32 c)

            // final step
            let h4 = rol h3
            let h5 = (byte h4 &&& byte 0xFF) ^^^ byte c
            let h6 = h4 &&& uint32 0xFFFFFF00
            hash <- h6 ||| uint32 h5
        )
        hash

    let computeStringHashes() =
        let sb = new StringBuilder()
        sb.AppendLine("; This file is auto generated, don't modify it") |> ignore

        [
            // module names
            "kernel32.dll"
            "ntdll.dll"
            "kernelbase.dll"
            "SacaraVm.dll"

            // function names
            "GetProcessHeap"
            "RtlAllocateHeap"
            "VirtualAlloc"
            "VirtualFree"
            "VirtualProtect"
            "RtlFreeHeap"
            "GetCurrentProcess"
            "GetModuleHandleW"
            "LoadLibraryA"
            "GetModuleInformation"
        ] 
        |> List.map(fun name -> (name, hashString(name)))
        |> List.iter(fun (name, hash) ->
            let cleanName = name.Replace('.', '_')
            sb.AppendFormat("hash_{0} EQU 0{1}h", cleanName, hash.ToString("X")).AppendLine() |> ignore
        )

        // copy file
        let fileContent = sb.ToString()
        let curDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
        let vmSrcFile = Path.Combine(curDir, "..", "..", "..", "SacaraVm", "strings.inc")
        writeFile(vmSrcFile, fileContent)
        Console.WriteLine("Files copied to: " + vmSrcFile)

    [<EntryPoint>]
    let main argv =         
        computeStringHashes()

        let opCodes = generateOpCodes()        
        saveOpCodeInAssemblerDir(opCodes)
        saveOpCodeInVmDir(opCodes)
        0
