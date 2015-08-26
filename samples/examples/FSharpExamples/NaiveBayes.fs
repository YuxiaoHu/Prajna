﻿(*---------------------------------------------------------------------------
    Copyright 2013 Microsoft

    Licensed under the Apache License, Version 2.0 (the "License");
    you may not use this file except in compliance with the License.
    You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law or agreed to in writing, software
    distributed under the License is distributed on an "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    See the License for the specific language governing permissions and
    limitations under the License.                                                      

    File: 
        NaiveBayes.fs
  
    Description: 
        Compute histograms for a Naive Bayes classifier
 ---------------------------------------------------------------------------*)
namespace Prajna.Examples.FSharp

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO

open Prajna.Core
open Prajna.Api.FSharp

open Prajna.Examples.Common

/// A Counts object holds word counts for a single class
/// A counts: Count[] holds counts for class k in in counts.[k]
type internal Counts = Dictionary<string, int>

/// <summary> 
/// This sample demonstrates Prajna functionality by implementing three versions of the  
/// <a href="https://en.wikipedia.org/wiki/Naive_Bayes_classifier">Naïve Bayes</a> 
/// algorithm: one using F# Seq, a very similar one using DSet.fold, and a third using DSet.mapReduce.
/// Naive Bayes is usually implemented as a baseline with which to compare other machine learning algorithms, 
/// or as an introductory to ML algorithms due to its siplicity.
/// The "training" phase accumulates the number of times each word appears on each class, and "predict"
/// simply computes P(class|word), for all words, and P(class), and multiplies it all together, pretending
/// the words are all uncorrelated.
/// </summary>
type NaiveBayes() =

    // A tiny fraction of 20 Newsgroups dataset data (http://archive.ics.uci.edu/ml/datasets/Twenty+Newsgroups)
    // slightly processed to have one example per line and eliminite newlines in example
    let data = Path.Combine(Utility.GetExecutingDir(), "20news-featurized2-tiny.txt")

    let split (c: char) (str:string) = str.Split([|c|], StringSplitOptions.RemoveEmptyEntries)

    let add word n (counts: Counts) = 
        match counts.TryGetValue word with
        | true, c -> counts.[word] <- c + n
        | _ -> counts.[word] <- n

    let numClasses = 20

    // The processing seems to have left some newlines in the file, so we use this to filter them out
    let chooseLine (line: string) =
        match split '\t' line with
        | [|_; label; text|] -> 
            let splitLine = split ' ' text
            Some(HashSet<string>(splitLine), Int32.Parse label)
        | _ -> 
            None

    // Adds all the words of an example to the class counts dictionary.
    // This is used both in the Seq and DSet versions.
    // We want to accumulate a separate Counts[] per partition, so we start start a DSet.fold with null,
    // which is passed to each partition, and initialize the running object on the first call.
    // This prevents Prajna from having to deserialize the zero object multiple times, once per partition,
    // at the cost of a null check per element. 
    let addWords (numLabels: int) (countsOrNull: Counts[]) (words: HashSet<string>, label: int) = 
        let counts = 
            if countsOrNull = null 
            then Array.init numLabels (fun _ -> new Counts()) 
            else countsOrNull
        words |> Seq.iter (fun w -> counts.[label] |> add w 1)
        counts

    // Adds two intermediate dictionaries, at the final "reduce" step of the parallel fold.
    // Used only in the DSet version.        
    let addCounts (counts1: Counts[]) (counts2: Counts[]) =
        let ret = 
            Array.map2 (fun (lc1: Counts) (lc2: Counts) ->
                for wc in lc2 do 
                    if wc.Key <> null then 
                        lc1 |> add wc.Key wc.Value
                lc1)
                counts1 counts2 
        ret

    // This is the full MapReduce version, in two Map-Reduce steps.
    let naiveBayesMapReduce (name: string) (cluster: Cluster) (trainSet: string seq) =
        let sparseCounts = 
            DSet<string>(Name = name, Cluster = cluster)
            |> DSet.distributeN 8 trainSet
            |> DSet.choose chooseLine
            |> DSet.mapReduce 
                (fun (words,label) -> seq { for w in words -> w,label } )
                (fun (word, labels) -> 
                    let histogram : int[] = Array.zeroCreate numClasses
                    for l in labels do
                        histogram.[l] <- histogram.[l] + 1
                    word, histogram)
            |> DSet.mapReduce 
                (fun (word,hist) -> 
                    seq {for i = 0 to hist.Length-1 do 
                            if hist.[i] <> 0 then
                                yield i,(word,hist.[i]) } )
                (fun (label,wordCounts) -> 
                    let cs = new Counts()
                    for w,c in wordCounts do 
                        if c <> 0 then
                            cs |> add w c
                    label,cs)
            |> DSet.toSeq
        let ret : Counts[] = Array.zeroCreate numClasses
        for i,cs in sparseCounts do
            ret.[i] <- cs
        ret |> Array.iteri (fun i cs -> if cs = null then ret.[i] <- Counts())
        ret

    let run (cluster: Cluster) = 

        // We take only the first 200 lines for speed, since this is run as a unit test with build
        let trainSet, testSet = 
            let all = 
                data 
                |> File.ReadLines 
                |> Seq.toArray
            let numTrain = 200
            all |> Seq.take numTrain |> Seq.toArray, all |> Seq.skip numTrain |> Seq.toArray

        // Both the Seq and DSet versions have the same structure: throw away a few badly formatted
        // lines then make a single call to fold...
        let sw = Stopwatch.StartNew()
        let seqCounts = 
            trainSet
            |> Seq.choose chooseLine
            |> Seq.fold (addWords numClasses) null
        printfn "Seq train took: %A" (sw.Stop(); sw.Elapsed)

        // ...only difference is that the DSet version needs a second "reducer" function
        // to do sum up intermediate per-partition results.
        // DSet.distributeN will create N partitions per node.
        let name = "20News-TinyTest-" + Guid.NewGuid().ToString("D")
        sw.Restart()
        let dsetCounts = 
            DSet<string>(Name = name, Cluster = cluster)
            |> DSet.distributeN 8 trainSet
            |> DSet.choose chooseLine
            |> DSet.fold (addWords numClasses) addCounts null
        printfn "DSet train took: %A" (sw.Stop(); sw.Elapsed)

        sw.Restart()
        let mapReduceCounts = naiveBayesMapReduce name cluster trainSet
        printfn "MapReduce train took: %A" (sw.Stop(); sw.Elapsed)

        // All versions should yield the exact same result.
        // As can be seen above, even though the algorithm *can* be expressed as map-reduce,
        // it is simpler and more natural as a fold.
        let areEqual = 
            let dictToMap (dict: Dictionary<_,_>) = seq {for kvPair in dict -> kvPair.Key, kvPair.Value} |> Map.ofSeq
            Seq.zip3 seqCounts dsetCounts mapReduceCounts
            |> Seq.map (fun (seqLabelCounts, dsetLabelCounts, mapReduceLabelCounts) -> 
                let seqMap = dictToMap seqLabelCounts
                let dsetMap = dictToMap dsetLabelCounts
                let mrMap = dictToMap mapReduceLabelCounts
                seqMap = dsetMap && dsetMap = mrMap)
            |> Seq.forall id
        printfn "Model comparison result: %s" (if areEqual then "Equal" else "Different")

        // Call this to test prediction accuracy on large dataset, but not during unit test
        let evaluate() =
            printfn "Testing..."

            // A class' "prior" is simply the probability of the class in the dataset overall,
            // before we look at any words
            let priors: float[] = 
                let labelCounts = 
                    trainSet
                    |> Seq.choose chooseLine 
                    |> Seq.map snd
                    |> Seq.countBy id
                    |> Seq.sortBy fst
                    |> Seq.map snd
                    |> Seq.toArray
                let sum = labelCounts |> Array.sum |> float
                labelCounts |> Array.map (fun x -> float x / sum)

            // For each example in the *test* set, return the actual label, predicted label, and predicted probabilities,
            // in this order.
            let preds : (int * int * float[])[] = 
                [|for words,label in testSet |> Seq.choose chooseLine do
                    // Do the multiplications in log space to avoid numerical instability.
                    // Using log(a * b) = log(a) + log(b)
                    let logProbs : float[] = Array.zeroCreate numClasses
                    for w in words do
                        let wCounts : int[] = 
                            seqCounts 
                            |> Array.map (fun labelCounts -> 
                                match labelCounts.TryGetValue w with
                                | true, c -> c
                                | _ -> 0)
                        let sum = Array.sum wCounts |> float
                        wCounts |> Seq.iteri (fun i c -> logProbs.[i] <- logProbs.[i] + Math.Log(float (c + 1) / (sum + 1.0)))
                    let normProbs = 
                        // Remember to multiply by the prior
                        let probs = Array.map2 (fun logProb prior -> Math.Exp logProb * prior) logProbs priors 
                        let probSum = probs |> Seq.sum
                        probs |> Array.map (fun p -> p / probSum)
                    let prediction = Array.IndexOf(normProbs, Array.max normProbs)
                    yield label, prediction, normProbs |]

            let hits = preds |> Seq.where (fun (l,p,_) -> l = p) |> Seq.length
            let accuracy = float hits / float (preds.Length)
            printfn "Accuracy: %f" accuracy

        areEqual
            
    interface IExample with
        member this.Description = 
            "Create Naive Bayes model"
        member this.Run(cluster) =
            run cluster        
        

    