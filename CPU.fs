module CPU
    open IStack
    open System
    open System.Collections.Generic
    let maxNeedleLength = 
        let keywordsMaxLength = Config.keywords |> List.map (fun A -> A.Length) |> List.max
        let signaturesMaxLength = Config.signatures |> List.map (fun A -> A.Length) |> List.max
        max keywordsMaxLength signaturesMaxLength            
    let joinedNeedles = Config.keywords @ Config.signatures 

    type CPUStack(blocks, needles: IList<string>, matchAll) =
        inherit IStack(blocks, needles)

        let searchByteSeqs (cluster: byte []) =
            Config.signatures
                |> List.filter (fun needle -> 
                    let needleLength = needle.Length
                    let clusterLength = cluster.Length
                    let rec containsNeedle i =
                        if i + needleLength > clusterLength then false
                        else if Array.sub cluster i needleLength = needle then true
                        else containsNeedle (i + 1)
                    containsNeedle 0)         
                |> List.length
                |> (*) 2

        let searchNeedles (s:string) =
            if matchAll then
                if (needles |> Seq.forall s.Contains) then needles.Count else 0
            else
                (needles |> Seq.filter s.Contains |> Seq.length)

        let inspectSector(sector: ReadOnlyMemory<byte>) =        
            let haystack = sector.ToArray()
            //let dft = DFT.DFTSearcher(haystack, maxNeedleLength)
            //joinedNeedles
            //    |> List.map (fun needle -> dft.Search(needle).Length)
            //    |> List.sum

            let s = System.Text.Encoding.ASCII.GetString(haystack)
            let utf16 = System.Text.Encoding.Unicode.GetString(haystack)
            searchNeedles s + searchNeedles utf16 + searchByteSeqs (haystack)
            
        override _.Scan(buffer: byte [], position: int64) =
            let sectors = buffer.Length / 512
            [0..sectors - 1]
                |> List.map (fun i -> 
                        let sector = ReadOnlyMemory<byte>(buffer, i * 512, 512)
                        inspectSector(sector) |> byte
                    )
                |> List.toArray
        override _.SearchIn (arg: string): int = searchNeedles arg            
