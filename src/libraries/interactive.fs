﻿namespace TheGamma.Interactive

open Fable.Helpers
open Fable.Import.Browser

open TheGamma.Common
open TheGamma.Series
open TheGamma.Html
open TheGamma.Interactive.Compost
module FsOption = Microsoft.FSharp.Core.Option

module InteractiveHelpers =
  let showAppAsync outputId size (data:series<_, _>) initial render update = async { 
    let id = "container" + System.Guid.NewGuid().ToString().Replace("-", "")
    h?div ["id" => id] [ ] |> renderTo (document.getElementById(outputId))        

    // Get data & wait until the element is created
    let! data = data.data |> Async.AwaitFuture 
    let mutable i = 10
    while i > 0 && document.getElementById(id) = null do
      do! Async.Sleep(10)
      i <- i - 1
    let element = document.getElementById(id)
    let size = 
      ( match size with Some w, _ -> w | _ -> element.clientWidth ),
      ( match size with _, Some h -> h | _ -> max 400. (element.clientWidth / 2.) ) 
    do
      try
        Compost.app outputId (initial data) (render data size) (update data)
      with e ->
        Log.exn("GUI", "Interactive rendering failed: %O", e) } 

  let showApp outputId size data initial render update = 
    showAppAsync outputId size data initial render update |> Async.StartImmediate 

  let showStaticAppAsync outputId size render = 
    showAppAsync outputId size (series<int, int>.create(async.Return [||], "", "", ""))
      (fun _ -> ())
      (fun _ size _ _ -> render size)
      (fun _ _ _ -> ())

  let showStaticApp outputId size data render = 
    showApp outputId size data
      (fun _ -> ())
      (fun data size _ _ -> render data size)
      (fun _ _ _ -> ())

  let calclateMax maxValue data = 
    let max = match maxValue with Some m -> m | _ -> Seq.max (Seq.map snd data)
    snd (Scales.adjustRange (0.0, max))

module CompostHelpers = 
  let (|Cont|) = function COV(CO x) -> x | _ -> failwith "Expected continuous value"
  let (|Cat|) = function CAR(CA x, r) -> x, r | _ -> failwith "Expected categorical value"
  let Cont x = COV(CO x)
  let Cat(x, r) = CAR(CA x, r)
  let orElse (a:option<_>) b = if a.IsSome then a else b
  let vega10 = [| "#1f77b4"; "#ff7f0e"; "#2ca02c"; "#d62728"; "#9467bd"; "#8c564b"; "#e377c2"; "#7f7f7f"; "#bcbd22"; "#17becf" |]
  let infinitely s = 
    if Seq.isEmpty s then seq { while true do yield "black" }
    else seq { while true do yield! s }

open CompostHelpers

// ------------------------------------------------------------------------------------------------
// Ordinary charts
// ------------------------------------------------------------------------------------------------

type AxisOptions = 
  { minValue : obj option
    maxValue : obj option 
    label : string option }
  static member Default = { minValue = None; maxValue = None; label = None }

type LegendOptions = 
  { position : string }
  static member Default = { position = "none" }

type ChartOptions =
  { size : float option * float option 
    xAxis : AxisOptions
    yAxis : AxisOptions 
    title : string option
    legend : LegendOptions } 
  static member Default = 
    { size = None, None; title = None
      legend = LegendOptions.Default
      xAxis = AxisOptions.Default
      yAxis = AxisOptions.Default }

module Internal = 
  
  // Helpers

  let applyScales xdata ydata chartOptions chart = 
    let getInnerScale axis data = 
      if axis.minValue = None && axis.maxValue = None then None
      else
        let numbers = Array.map dateOrNumberAsNumber data
        let extremes = Seq.min numbers, Seq.max numbers
        let lo, hi = Scales.adjustRange extremes
        let lo, hi = defaultArg axis.minValue (box lo), defaultArg axis.maxValue (box hi)
        Some(CO(unbox lo), CO(unbox hi))

    let sx = getInnerScale chartOptions.xAxis xdata
    let sy = getInnerScale chartOptions.yAxis ydata
    AutoScale(sx.IsNone, sy.IsNone, InnerScale(sx, sy, chart))      

  let applyAxes xdata ydata chart = 
    let style data =
      let isDate = data |> Seq.exists isDate
      if isDate then
        let values = data |> Array.map dateOrNumberAsNumber
        let lo, hi = asDate(Seq.min values), asDate(Seq.max values)
        if (hi - lo).TotalDays <= 1. then
          fun _ (Cont v) -> formatTime(asDate(v))
        else
          fun _ (Cont v) -> formatDate(asDate(v))
      else Compost.defaultFormat
    Style(
      (fun s -> 
        { s with 
            FormatAxisXLabel = style xdata
            FormatAxisYLabel = style ydata }),
      Axes(true, true, chart) )

  let applyStyle f chart = 
    Style(f, chart)

  let applyLegend (width, height) chartOptions labels chart =     
    if chartOptions.legend.position = "right" then
      let labels = Array.ofSeq labels
      let labs = 
        InnerScale(Some(CO 0., CO 100.), None, 
            Layered
              [ for clr, lbl in labels do
                  let style clr = applyStyle (fun s -> { s with Font = "9pt sans-serif"; Fill=Solid(1., HTML clr); StrokeColor=(0.0, RGB(0,0,0)) })
                  yield Padding((4., 0., 4., 0.), Bar(CO 4., CA lbl)) |> style clr
                  yield Text(COV(CO 5.), CAR(CA lbl, 0.5), VerticalAlign.Middle, HorizontalAlign.Start, 0., lbl) |> style "black"
              ] ) 
      let lwid, lhgt = (width - 250.) / width, (float labels.Length * 20.) / height    
      console.log(lwid, lhgt)
      Layered
        [ OuterScale(Some(Continuous(CO 0.0, CO lwid)), Some(Continuous(CO 0.0, CO 1.0)), chart)
          OuterScale(Some(Continuous(CO lwid, CO 1.0)), Some(Continuous(CO 0.0, CO lhgt)), labs)  ]
    else chart

  let applyLabels chartOptions xdata ydata chart =     
    let lowMidHigh axis data = 
      if Array.forall (fun v -> isNumber v || isDate v) data then 
        let data = Array.map dateOrNumberAsNumber data 
        let lo, hi = Seq.min data, Seq.max data
        let lo, hi = Scales.adjustRange (lo, hi)
        let lo = if axis.minValue.IsSome then dateOrNumberAsNumber (axis.minValue.Value) else lo
        let hi = if axis.maxValue.IsSome then dateOrNumberAsNumber (axis.maxValue.Value) else hi
        COV(CO lo),
        COV(CO ((lo + hi) / 2.)),
        COV(CO hi)
      else 
        CAR(CA (string (data.[0])), 0.0),
        ( if data.Length % 2 = 1 then CAR(CA (string (data.[data.Length/2])), 0.5)
          else CAR(CA (string (data.[data.Length/2])), 0.0) ),
        CAR(CA (string (data.[data.Length-1])), 1.0)
    let lmhx = lazy lowMidHigh chartOptions.xAxis xdata
    let lmhy = lazy lowMidHigh chartOptions.yAxis ydata
    let plo, pmid, phi = (fun (v,_,_) -> v), (fun (_,v,_) -> v), (fun (_,_,v) -> v)

    let lblStyle font chart = 
      // Apply padding as if the label was inside chart area
      Padding((300., 20., 40., 100.), chart)
      |> applyStyle (fun s -> { s with StrokeWidth = Pixels 0; Fill = Solid(1., HTML "black"); Font = font })

    // X axis label, Y axis label
    let chart, pbot = 
      match chartOptions.xAxis.label with
      | Some xl -> 
          let lbl = Text(pmid lmhx.Value, plo lmhy.Value, VerticalAlign.Middle, HorizontalAlign.Center, 0.0, xl)
          Layered [ chart; lblStyle "bold 9pt sans-serif" lbl ], 40.
      | _ -> chart, 0.
    let chart, pleft = 
      match chartOptions.yAxis.label with
      | Some yl -> 
          let lbl = Text(plo lmhx.Value, pmid lmhy.Value, VerticalAlign.Middle, HorizontalAlign.Center, -90.0, yl)
          Layered [ chart; lblStyle "bold 9pt sans-serif" lbl ], 50.
      | _ -> chart, 0.

    // Chart title
    let chart, ptop = 
      match chartOptions.title with 
      | None -> chart, 0.
      | Some title ->
          let ttl = Text(pmid lmhx.Value, phi lmhy.Value, VerticalAlign.Hanging, HorizontalAlign.Center, 0.0, title)
          Layered [ chart; lblStyle "13pt sans-serif" ttl ], 0.

    Padding((ptop, 0., pbot, pleft), chart)

module Charts = 
  open Internal

  let createChart size chart =   
    h?div ["style"=>"text-align:center;padding-top:20px"] [
      Compost.createSvg size chart
    ]
        
  let inAxis axis value =
    if axis.minValue.IsSome && dateOrNumberAsNumber value < dateOrNumberAsNumber axis.minValue.Value then false
    elif axis.maxValue.IsSome && dateOrNumberAsNumber value > dateOrNumberAsNumber axis.maxValue.Value then false
    else true
                
  // Charts

  let renderBubbles chartOptions size bc (data:(obj * obj * obj option)[]) =   
    let xdata, ydata = Array.map (fun (x, _, _) -> x) data, Array.map (fun (_, y, _) -> y) data
    Layered [
      for x, y, s in data do
        if inAxis chartOptions.xAxis x && inAxis chartOptions.yAxis y then
          let size = unbox (defaultArg s (box 2.))
          yield Bubble(COV(CO (dateOrNumberAsNumber x)), COV(CO (dateOrNumberAsNumber y)), size, size) ]
    |> applyScales xdata ydata chartOptions 
    |> applyAxes xdata ydata
    |> applyLabels chartOptions xdata ydata 
    |> applyStyle (fun s -> { s with StrokeWidth = Pixels 0; Fill = Solid(0.6, HTML bc) })            
    // |> applyLegend chartOptions
    |> createChart size 

  let renderLines chartOptions size lcs labels (data:(obj * obj)[][]) =   
    let xdata, ydata = Array.collect (Array.map fst) data, Array.collect (Array.map snd) data    
    Layered [
      for clr, line in Seq.zip (infinitely lcs) data do
        let points = 
          [ for x, y in line do
              if inAxis chartOptions.xAxis x && inAxis chartOptions.yAxis y then
                yield COV(CO (dateOrNumberAsNumber x)), COV(CO (dateOrNumberAsNumber y)) ]
        if not (List.isEmpty points) then 
          yield Line points |> applyStyle (fun s -> { s with StrokeColor = 1.0, HTML clr }) ]
    |> applyScales xdata ydata chartOptions 
    |> applyAxes xdata ydata
    |> applyLabels chartOptions xdata ydata 
    |> applyStyle (fun s -> { s with StrokeWidth = Pixels 2 })
    |> applyLegend size chartOptions (Seq.zip (infinitely lcs) labels)
    |> createChart size 

  let renderColsBars isBar chartOptions size clrs labels (data:(string * float)[]) =   
    let xdata, ydata = 
      if isBar then Array.map (snd >> box) data, Array.map (fst >> box) data    
      else Array.map (fst >> box) data, Array.map (snd >> box) data    
    Layered [
      for clr, (lbl, v) in Seq.zip (infinitely clrs) data ->
        ( if isBar then Padding((6.,0.,6.,1.), Bar(CO v, CA lbl))
          else Padding((0.,6.,1.,6.), Column(CA lbl, CO v)) )
        |> applyStyle (fun s -> { s with Fill = Solid(1.0, HTML clr) }) ]
    |> applyScales xdata ydata chartOptions 
    |> applyAxes xdata ydata
    |> applyLabels chartOptions (unbox xdata) (unbox ydata)
    |> applyLegend size chartOptions (Seq.zip (infinitely clrs) labels)
    |> createChart size 

// ------------------------------------------------------------------------------------------------
// You Guess Line
// ------------------------------------------------------------------------------------------------

module YouDrawHelpers = 
  type YouDrawEvent = 
    | ShowResults
    | Draw of float * float

  type YouDrawState = 
    { Completed : bool
      Clip : float
      Data : (float * float)[]
      XData : obj[]
      YData : obj[]
      Guessed : (float * option<float>)[] 
      IsKeyDate : bool }

  let initState data clipx = 
    let isDate = data |> Seq.exists (fst >> isDate)
    let data = data |> Array.map (fun (k, v) -> dateOrNumberAsNumber k, v)
    { Completed = false
      Data = data
      XData = Array.map (fst >> box) data
      YData = Array.map (snd >> box) data
      Clip = clipx
      IsKeyDate = isDate
      Guessed = [| for x, y in data do if x > clipx then yield x, None |] }

  let handler state evt = 
    match evt with
    | ShowResults -> { state with Completed = true }
    | Draw (downX, downY) ->
        let indexed = Array.indexed state.Guessed
        let nearest, _ = indexed |> Array.minBy (fun (_, (x, _)) -> abs (downX - x))
        { state with
            Guessed = indexed |> Array.map (fun (i, (x, y)) -> 
              if i = nearest then (x, Some downY) else (x, y)) }

  let render chartOptions (width, height) (markers:(float*obj)[]) (topLbl, leftLbl, rightLbl) 
    (leftClr,rightClr,guessClr,markClr) (loy, hiy) trigger state = 

    let all = 
      [| for x, y in state.Data -> Cont x, Cont y |]
    let known = 
      [| for x, y in state.Data do if x <= state.Clip then yield Cont x, Cont y |]
    let right = 
      [| yield Array.last known
         for x, y in state.Data do if x > state.Clip then yield Cont x, Cont y |]
    let guessed = 
      [| yield Array.last known
         for x, y in state.Guessed do if y.IsSome then yield Cont x, Cont y.Value |]

    let lx, ly = (fst (Seq.head state.Data) + float state.Clip) / 2., loy + (hiy - loy) / 10.
    let rx, ry = (fst (Seq.last state.Data) + float state.Clip) / 2., loy + (hiy - loy) / 10.
    let tx, ty = float state.Clip, hiy - (hiy - loy) / 10.
    let setColor c s = { s with Font = "12pt sans-serif"; Fill=Solid(1.0, HTML c); StrokeColor=(0.0, RGB(0,0,0)) }
    let labels = 
      Shape.Layered [
        Style(setColor leftClr, Shape.Text(COV(CO lx), COV(CO ly), VerticalAlign.Baseline, HorizontalAlign.Center, 0., leftLbl))
        Style(setColor rightClr, Shape.Text(COV(CO rx), COV(CO ry), VerticalAlign.Baseline, HorizontalAlign.Center, 0., rightLbl))
        Style(setColor guessClr, Shape.Text(COV(CO tx), COV(CO ty), VerticalAlign.Baseline, HorizontalAlign.Center, 0., topLbl))
      ]

    let LineStyle shape = 
      Style((fun s -> 
        { s with 
            Fill = Solid(1.0, HTML "transparent"); 
            StrokeWidth = Pixels 2; 
            StrokeDashArray = [Integer 5; Integer 5]
            StrokeColor=0.6, HTML markClr }), shape)
    let FontStyle shape = 
      Style((fun s -> { s with Font = "11pt sans-serif"; Fill = Solid(1.0, HTML markClr); StrokeColor = 0.0, HTML "transparent" }), shape)
    
    let loln, hiln = Scales.adjustRange (loy, hiy)
    let markers = [
        for i, (x, lbl) in Seq.mapi (fun i v -> i, v) markers do
          let kl, kt = if i % 2 = 0 then 0.90, 0.95 else 0.80, 0.85
          let ytx = loln + (hiln - loln) * kt
          let hiln = loln + (hiln - loln) * kl
          yield Line [(COV(CO x), COV(CO loln)); (COV(CO x), COV(CO hiln))] |> LineStyle
          yield Text(COV(CO x), COV(CO ytx), VerticalAlign.Middle, HorizontalAlign.Center, 0., string lbl) |> FontStyle
      ]

    let coreChart = 
      Interactive(
        ( if state.Completed then []
          else
            [ MouseMove(fun evt (Cont x, Cont y) -> 
                if (int evt.buttons) &&& 1 = 1 then trigger(Draw(x, y)) )
              TouchMove(fun evt (Cont x, Cont y) -> 
                trigger(Draw(x, y)) )
              MouseDown(fun evt (Cont x, Cont y) -> trigger(Draw(x, y)) )
              TouchStart(fun evt (Cont x, Cont y) -> trigger(Draw(x, y)) ) ]),
        Shape.InnerScale
          ( None, Some(CO loy, CO hiy), 
            Layered [
              yield labels
              yield! markers
              yield Style(Drawing.hideFill >> Drawing.hideStroke, Line all)
              yield Style(
                (fun s -> { s with StrokeColor = (1.0, HTML leftClr); Fill = Solid(0.2, HTML leftClr) }), 
                Layered [ Area known; Line known ]) 
              if state.Completed then
                yield Style((fun s -> 
                  { s with 
                      StrokeColor = (1.0, HTML rightClr)
                      StrokeDashArray = [ Percentage 0.; Percentage 100. ]
                      Fill = Solid(0.0, HTML rightClr)
                      Animation = Some(1000, "ease", fun s -> 
                        { s with
                            StrokeDashArray = [ Percentage 100.; Percentage 0. ]
                            Fill = Solid(0.2, HTML rightClr) } 
                      ) }), 
                  Layered [ Area right; Line right ])                 
              if guessed.Length > 1 then
                yield Style(
                  (fun s -> { s with StrokeColor = (1.0, HTML guessClr); StrokeDashArray = [ Integer 5; Integer 5 ] }), 
                  Line guessed ) 
            ]) )
    
    let chart = 
      Style(
        (fun s -> 
          if state.IsKeyDate then 
            let lo, hi = asDate(fst state.Data.[0]), asDate(fst state.Data.[state.Data.Length-1])
            if (hi - lo).TotalDays <= 1. then
              { s with FormatAxisXLabel = fun _ (Cont v) -> formatTime(asDate(v)) }
            else
              { s with FormatAxisXLabel = fun _ (Cont v) -> formatDate(asDate(v)) }
          else s),
        Axes(true, true, 
          AutoScale(false, true, coreChart)))
    
    let chart = 
      chart 
      |> Internal.applyLabels chartOptions state.XData state.YData
    
    h?div ["style"=>"text-align:center;padding-top:20px"] [
      Compost.createSvg (width, height) chart
      h?div ["style"=>"padding-bottom:20px"] [
        h?button [
            yield "type" => "button"
            yield "click" =!> fun _ _ -> trigger ShowResults
            if state.Guessed |> Seq.last |> snd = None then
              yield "disabled" => "disabled"
          ] [ text "Show me how I did" ]
        ]
    ]
      
// ------------------------------------------------------------------------------------------------
// You Guess Bar & You Guess Column
// ------------------------------------------------------------------------------------------------

module YouGuessColsHelpers = 

  type YouGuessState = 
    { Completed : bool
      CompletionStep : float
      Default : float
      Maximum : float
      Data : (string * float)[]
      Guesses : Map<string, float> }

  type YouGuessEvent = 
    | ShowResults 
    | Animate 
    | Update of string * float

  let initState data maxValue =     
    { Completed = false
      CompletionStep = 0.0
      Data = data 
      Default = Array.averageBy snd data
      Maximum = InteractiveHelpers.calclateMax maxValue data
      Guesses = Map.empty }

  let update state evt = 
    match evt with
    | ShowResults -> { state with Completed = true }
    | Animate -> { state with CompletionStep = min 1.0 (state.CompletionStep + 0.05) }
    | Update(k, v) -> { state with Guesses = Map.add k v state.Guesses }

  let renderCols (width, height) topLabel trigger state = 
    if state.Completed && state.CompletionStep < 1.0 then
      window.setTimeout((fun () -> trigger Animate), 50) |> ignore
    let chart = 
      Axes(true, true, 
        AutoScale(false, true, 
          Interactive
            ( ( if state.Completed then []
                else
                  [ EventHandler.MouseMove(fun evt (Cat(x, _), Cont y) ->
                      if (int evt.buttons) &&& 1 = 1 then trigger (Update(x, y)) )
                    EventHandler.MouseDown(fun evt (Cat(x, _), Cont y) ->
                      trigger (Update(x, y)) )
                    EventHandler.TouchStart(fun evt (Cat(x, _), Cont y) ->
                      trigger (Update(x, y)) )
                    EventHandler.TouchMove(fun evt (Cat(x, _), Cont y) ->
                      trigger (Update(x, y)) ) ] ),
              Style
                ( (fun s -> if state.Completed then s else { s with Cursor = "row-resize" }),
                  (Layered [
                    yield Stack
                      ( Horizontal, 
                        [ for clr, (lbl, value) in Seq.zip (infinitely vega10) state.Data -> 
                            let sh = Style((fun s -> { s with Fill = Solid(0.2, HTML "#a0a0a0") }), Column(CA lbl, CO state.Maximum )) 
                            Shape.Padding((0., 10., 0., 10.), sh) ])
                    yield Stack
                      ( Horizontal, 
                        [ for clr, (lbl, value) in Seq.zip (infinitely vega10) state.Data -> 
                            let alpha, value = 
                              match state.Completed, state.Guesses.TryFind lbl with
                              | true, Some guess -> 0.6, state.CompletionStep * value + (1.0 - state.CompletionStep) * guess
                              | _, Some v -> 0.6, v
                              | _, None -> 0.2, state.Default
                            let sh = Style((fun s -> { s with Fill = Solid(alpha, HTML clr) }), Column(CA lbl, CO value)) 
                            Shape.Padding((0., 10., 0., 10.), sh) ])
                    for clr, (lbl, value) in Seq.zip (infinitely vega10) state.Data do
                      match state.Guesses.TryFind lbl with
                      | None -> () 
                      | Some guess ->
                          let line = Line [ CAR(CA lbl, 0.0), COV (CO guess); CAR(CA lbl, 1.0), COV (CO guess) ]
                          yield Style(
                            (fun s -> 
                              { s with
                                  StrokeColor = (1.0, HTML clr)
                                  StrokeWidth = Pixels 4
                                  StrokeDashArray = [ Integer 5; Integer 5 ] }), 
                            Shape.Padding((0., 10., 0., 10.), line))
                    match topLabel with
                    | None -> ()
                    | Some lbl ->
                        let x = CAR(CA (fst state.Data.[state.Data.Length/2]), if state.Data.Length % 2 = 0 then 0.0 else 0.5)
                        let y = COV(CO (state.Maximum * 0.9))
                        yield Style(
                          (fun s -> { s with Font = "13pt sans-serif"; Fill=Solid(1.0, HTML "#808080"); StrokeColor=(0.0, RGB(0,0,0)) }),
                          Text(x, y, VerticalAlign.Baseline, HorizontalAlign.Center, 0., lbl) )
                  ]) ))))

    h?div ["style"=>"text-align:center;padding-top:20px"] [
      Compost.createSvg (width, height) chart
      h?div ["style"=>"padding-bottom:20px"] [
        h?button [
            yield "type" => "button"
            yield "click" =!> fun _ _ -> trigger ShowResults
            if state.Guesses.Count <> state.Data.Length then
              yield "disabled" => "disabled"
          ] [ text "Show me how I did" ]
        ]
    ]


  let renderBars (width, height) topLabel trigger state = 
    if state.Completed && state.CompletionStep < 1.0 then
      window.setTimeout((fun () -> trigger Animate), 50) |> ignore
    let chart = 
      Axes(true, false, 
        AutoScale(true, false, 
          Interactive
            ( ( if state.Completed then []
                else
                  [ EventHandler.MouseMove(fun evt (Cont x, Cat(y, _)) ->
                      if (int evt.buttons) &&& 1 = 1 then trigger (Update(y, x)) )
                    EventHandler.MouseDown(fun evt (Cont x, Cat(y, _)) ->
                      trigger (Update(y, x)) )
                    EventHandler.TouchStart(fun evt (Cont x, Cat(y, _)) ->
                      trigger (Update(y, x)) )
                    EventHandler.TouchMove(fun evt (Cont x, Cat(y, _)) ->
                      trigger (Update(y, x)) ) ] ),
              Style
                ( (fun s -> if state.Completed then s else { s with Cursor = "col-resize" }),
                  (Layered [
                    yield InnerScale(Some(CO 0., CO state.Maximum), None, 
                      Stack
                        ( Vertical, 
                          [ for clr, (lbl, value) in Seq.zip (infinitely vega10) state.Data -> 
                              let sh = Style((fun s -> { s with Fill = Solid(0.2, HTML "#a0a0a0") }), Bar(CO state.Maximum, CA lbl)) 
                              Shape.Padding((10., 0., 10., 0.), sh) ]))
                    yield Stack
                      ( Vertical, 
                        [ for clr, (lbl, value) in Seq.zip (infinitely vega10) state.Data -> 
                            let alpha, value = 
                              match state.Completed, state.Guesses.TryFind lbl with
                              | true, Some guess -> 0.6, state.CompletionStep * value + (1.0 - state.CompletionStep) * guess
                              | _, Some v -> 0.6, v
                              | _, None -> 0.2, state.Default
                            let sh = Style((fun s -> { s with Fill = Solid(alpha, HTML clr) }), Bar(CO value, CA lbl)) 
                            Shape.Padding((10., 0., 10., 0.), sh) ])

                    for clr, (lbl, _) in Seq.zip (infinitely vega10) state.Data do 
                        let x = COV(CO (state.Maximum * 0.95))
                        let y = CAR(CA lbl, 0.5)
                        yield Style(
                          (fun s -> { s with Font = "13pt sans-serif"; Fill=Solid(1.0, HTML clr); StrokeColor=(0.0, RGB(0,0,0)) }),
                          Text(x, y, VerticalAlign.Middle, HorizontalAlign.End, 0., lbl) )

                    for clr, (lbl, value) in Seq.zip (infinitely vega10) state.Data do
                      match state.Guesses.TryFind lbl with
                      | None -> () 
                      | Some guess ->
                          let line = Line [ COV (CO guess), CAR(CA lbl, 0.0); COV (CO guess), CAR(CA lbl, 1.0) ]
                          yield Style(
                            (fun s -> 
                              { s with
                                  StrokeColor = (1.0, HTML clr)
                                  StrokeWidth = Pixels 4
                                  StrokeDashArray = [ Integer 5; Integer 5 ] }), 
                            Shape.Padding((10., 0., 10., 0.), line))
                    match topLabel with
                    | None -> ()
                    | Some lbl ->
                        let x = COV(CO (state.Maximum * 0.9))
                        let y = CAR(CA (fst state.Data.[state.Data.Length/2]), if state.Data.Length % 2 = 0 then 0.0 else 0.5)
                        yield Style(
                          (fun s -> { s with Font = "13pt sans-serif"; Fill=Solid(1.0, HTML "#808080"); StrokeColor=(0.0, RGB(0,0,0)) }),
                          Text(x, y, VerticalAlign.Baseline, HorizontalAlign.Center, 0., lbl) )
                  ]) ))))

    h?div ["style"=>"text-align:center;padding-top:20px"] [
      Compost.createSvg (width, height) chart
      h?div ["style"=>"padding-bottom:20px"] [
        h?button [
            yield "type" => "button"
            yield "click" =!> fun _ _ -> trigger ShowResults
            if state.Guesses.Count <> state.Data.Length then
              yield "disabled" => "disabled"
          ] [ text "Show me how I did" ]
        ]
    ]

// ------------------------------------------------------------------------------------------------
// You Guess Sort Bars
// ------------------------------------------------------------------------------------------------

module YouGuessSortHelpers = 
  type YouGuessState = 
    { Data : (string * float)[] 
      Colors : System.Collections.Generic.IDictionary<string, string>
      Assignments : Map<string, string>
      Selected : string
      Maximum : float 
      CompletionStep : float
      Completed : bool }

  type YouGuessEvent = 
    | SelectItem of string
    | AssignCurrent of string
    | ShowResults 
    | Animate 

  let initState maxValue data =     
    { Data = data 
      CompletionStep = 0.0
      Completed = false
      Colors = Seq.map2 (fun (lbl, _) clr -> lbl, clr) data vega10 |> dict 
      Assignments = Map.empty
      Selected = fst (Seq.head data)
      Maximum = InteractiveHelpers.calclateMax maxValue data }

  let update state evt = 
    match evt with
    | Animate -> { state with CompletionStep = min 1.0 (state.CompletionStep + 0.05) }
    | ShowResults -> { state with Completed = true }
    | SelectItem s -> { state with Selected = s }
    | AssignCurrent target -> 
        let newAssigns = 
          state.Assignments
          |> Map.filter (fun _ v -> v <> state.Selected)
          |> Map.add target state.Selected
        let assigned = newAssigns |> Seq.map (fun kvp -> kvp.Value) |> set
        let newSelected = 
          state.Data
          |> Seq.map fst 
          |> Seq.filter (assigned.Contains >> not)
          |> Seq.tryHead
        { state with Assignments = newAssigns; Selected = defaultArg newSelected state.Selected }
  
  let renderBars (width, height) trigger (state:YouGuessState) = 
    if state.Completed && state.CompletionStep < 1.0 then
      window.setTimeout((fun () -> trigger Animate), 50) |> ignore
    let chart = 
      Axes(true, false, 
        AutoScale(true, false,
          Interactive
            ( ( if state.Completed then [] else
                  [ EventHandler.MouseDown(fun evt (_, Cat(y, _)) -> trigger(AssignCurrent y))
                    EventHandler.TouchStart(fun evt (_, Cat(y, _)) -> trigger(AssignCurrent y))
                    EventHandler.TouchMove(fun evt (_, Cat(y, _)) -> trigger(AssignCurrent y)) ]),
              Style
                ( (fun s -> if state.Completed then s else { s with Cursor = "pointer" }),
                  (Layered [
                    yield Stack
                      ( Vertical, 
                        [ for i, (lbl, original) in Seq.mapi (fun i v -> i, v) (Seq.sortBy snd state.Data) do
                            let alpha, value, clr = 
                              match state.Completed, state.Assignments.TryFind lbl with
                              | true, Some assigned -> 
                                  let _, actual = state.Data |> Seq.find (fun (lbl, _) -> lbl = assigned)
                                  0.6, state.CompletionStep * actual + (1.0 - state.CompletionStep) * original, state.Colors.[assigned]
                              | _, Some assigned -> 0.6, original, state.Colors.[assigned]
                              | _, None -> 0.3, original, "#a0a0a0"

                            if i = state.Data.Length - 1 && state.Assignments.Count = 0 then
                              let txt = Text(COV(CO(state.Maximum * 0.05)), CAR(CA lbl, 0.5), Middle, Start, 0., "Assign highlighted value to one of the bars by clicking on it!")
                              yield Style((fun s -> { s with Font = "13pt sans-serif"; Fill = Solid(1.0, HTML "#606060"); StrokeColor=(0.0, HTML "white") }), txt ) 

                            let sh = Style((fun s -> { s with Fill = Solid(alpha, HTML clr) }), Bar(CO value, CA lbl)) 
                            if clr <> "#a0a0a0" then
                              let line = Line [ COV (CO original), CAR(CA lbl, 0.0); COV (CO original), CAR(CA lbl, 1.0) ]
                              yield Style(
                                (fun s -> 
                                  { s with
                                      StrokeColor = (1.0, HTML clr)
                                      StrokeWidth = Pixels 4
                                      StrokeDashArray = [ Integer 5; Integer 5 ] }), 
                                Shape.Padding((5., 0., 5., 0.), line))
                            yield Shape.Padding((5., 0., 5., 0.), sh) ])
                  ]) ))))

    let labs = 
      Padding(
        (0., 20., 20., 25.),
        InnerScale(Some(CO 0., CO 100.), None, 
          Interactive(
            ( if state.Completed then [] else
                [ EventHandler.MouseDown(fun evt (_, Cat(lbl, _)) -> trigger (SelectItem lbl))
                  EventHandler.TouchStart(fun evt (_, Cat(lbl, _)) -> trigger (SelectItem lbl)) 
                  EventHandler.TouchMove(fun evt (_, Cat(lbl, _)) -> trigger (SelectItem lbl)) ]),
            Layered
              [ for lbl, _ in Seq.rev state.Data do
                  let clr = state.Colors.[lbl]
                  let x = COV(CO 5.)
                  let y = CAR(CA lbl, 0.5)
                  let af, al = if state.Completed || lbl = state.Selected then 0.9, 1.0 else 0.2, 0.6
                  yield 
                    Style((fun s -> { s with Fill=Solid(af, HTML clr)  }), 
                      Padding((2., 0., 2., 0.), Bar(CO 4., CA lbl)))
                  yield Style(
                    (fun s -> { s with Font = "11pt sans-serif"; Fill=Solid(al, HTML clr); StrokeColor=(0.0, RGB(0,0,0)) }),
                    Text(x, y, VerticalAlign.Middle, HorizontalAlign.Start, 0., lbl) ) 
                  if not state.Completed then
                    yield 
                      Style((fun s -> { s with Cursor="pointer"; Fill=Solid(0.0, HTML "white")  }), Bar(CO 100., CA lbl))
                ])))
             
    let all = 
      Layered
        [ OuterScale(None, Some(Continuous(CO 0.0, CO 3.0)), labs) 
          OuterScale(None, Some(Continuous(CO 3.0, CO 10.0)), chart) ]

    h?div ["style"=>"text-align:center;padding-top:20px"] [
      Compost.createSvg (width, height) all
      h?div ["style"=>"padding-bottom:20px"] [
        h?button [
            yield "type" => "button"
            yield "click" =!> fun _ _ -> trigger ShowResults
            if state.Assignments.Count <> state.Data.Length then
              yield "disabled" => "disabled"
          ] [ text "Show me how I did" ]
        ]
    ]
    
// ------------------------------------------------------------------------------------------------
// You Guess API
// ------------------------------------------------------------------------------------------------

open TheGamma.Series
open TheGamma.Common

type YouGuessColsBarsKind = Cols | Bars

type YouGuessColsBars =
  private 
    { kind : YouGuessColsBarsKind
      data : series<string, float> 
      maxValue : float option
      topLabel : string option
      size : float option * float option }
  member y.setLabel(top) = { y with topLabel = Some top }
  member y.setMaximum(max) = { y with maxValue = Some max }
  member y.setSize(?width, ?height) = 
    { y with size = (orElse width (fst y.size), orElse height (snd y.size)) }

  member y.show(outputId) =   
    InteractiveHelpers.showApp outputId y.size y.data
      (fun data -> YouGuessColsHelpers.initState data y.maxValue)
      (fun _ size -> 
          match y.kind with 
          | Bars -> YouGuessColsHelpers.renderBars size y.topLabel 
          | Cols -> YouGuessColsHelpers.renderCols size y.topLabel)
      (fun _ -> YouGuessColsHelpers.update)

type YouGuessLine = 
  private 
    { data : series<obj, float> 
      markers : series<float, obj> option
      clip : float option
      markerColor : string option
      knownColor : string option
      unknownColor : string option 
      drawColor : string option 
      topLabel : string option
      knownLabel : string option
      guessLabel : string option 
  // [copy-paste]
      options : ChartOptions }  
  member y.setTitle(title) =
    { y with options = { y.options with title = title } }
  member y.setLegend(position) = 
    { y with options = { y.options with legend = { position = position } } }
  member y.setSize(?width, ?height) = 
    { y with options = { y.options with size = (orElse width (fst y.options.size), orElse height (snd y.options.size)) } }
  member y.setAxisX(?minValue, ?maxValue, ?label) = 
    let ax = { y.options.xAxis with minValue = orElse minValue y.options.xAxis.minValue; maxValue = orElse maxValue y.options.xAxis.maxValue; label = orElse label y.options.xAxis.label }
    { y with options = { y.options with xAxis = ax } }
  member y.setAxisY(?minValue, ?maxValue, ?label) = 
    let ax = { y.options.yAxis with minValue = orElse minValue y.options.yAxis.minValue; maxValue = orElse maxValue y.options.yAxis.maxValue; label = orElse label y.options.yAxis.label }
    { y with options = { y.options with yAxis = ax } }
  // [/copy-paste]
  
  member y.setRange(min, max) = y.setAxisY(min, max) // TODO: Deprecated
  member y.setClip(clip) = { y with clip = Some (dateOrNumberAsNumber clip) }
  member y.setColors(known, unknown) = { y with knownColor = Some known; unknownColor = Some unknown }
  member y.setDrawColor(draw) = { y with drawColor = Some draw }
  member y.setMarkerColor(marker) = { y with markerColor = Some marker }
  member y.setLabels(top, known, guess) = { y with knownLabel = Some known; topLabel = Some top; guessLabel = Some guess }
  member y.setMarkers(markers) = { y with markers = Some markers }
  member y.show(outputId) = Async.StartImmediate <| async {
    let markers = defaultArg y.markers (series<string, float>.create(async.Return [||], "", "", ""))
    let! markers = markers.data |> Async.AwaitFuture
    let markers = markers |> Array.sortBy fst
    return! InteractiveHelpers.showAppAsync outputId y.options.size y.data
      (fun data ->
          let clipx = match y.clip with Some v -> v | _ -> dateOrNumberAsNumber (fst (data.[data.Length / 2]))
          YouDrawHelpers.initState (Array.sortBy (fst >> dateOrNumberAsNumber) data) clipx)
      (fun data size ->           
          let loy = match y.options.yAxis.minValue with Some v -> unbox v | _ -> data |> Seq.map snd |> Seq.min
          let hiy = match y.options.yAxis.maxValue with Some v -> unbox v | _ -> data |> Seq.map snd |> Seq.max       
          let lc, dc, gc, mc = 
            defaultArg y.knownColor "#606060", defaultArg y.unknownColor "#FFC700", 
            defaultArg y.drawColor "#808080", defaultArg y.markerColor "#C65E31"    
          let data = Array.sortBy (fst >> dateOrNumberAsNumber) data
          let co = { y.options with xAxis = { y.options.xAxis with minValue = Some (box (fst data.[0])); maxValue = Some (box (fst data.[data.Length-1])) } }
          YouDrawHelpers.render co size markers
            (defaultArg y.topLabel "", defaultArg y.knownLabel "", defaultArg y.guessLabel "") 
            (lc,dc,gc,mc) (loy, hiy)) 
      (fun _ -> YouDrawHelpers.handler) } 

type YouGuessSortBars = 
  private 
    { data : series<string, float> 
      maxValue : float option 
      size : float option * float option }
  member y.setMaximum(max) = { y with maxValue = Some max }
  member y.setSize(?width, ?height) = 
    { y with size = (orElse width (fst y.size), orElse height (snd y.size)) }
  member y.show(outputId) = 
    InteractiveHelpers.showApp outputId y.size y.data
      (YouGuessSortHelpers.initState y.maxValue)
      (fun _ size -> YouGuessSortHelpers.renderBars size)
      (fun _ -> YouGuessSortHelpers.update)

type youguess = 
  static member columns(data:series<string, float>) = 
    { YouGuessColsBars.data = data; topLabel = None; kind = Cols; maxValue = None; size = None, None }
  static member bars(data:series<string, float>) = 
    { YouGuessColsBars.data = data; topLabel = None; kind = Bars; maxValue = None; size = None, None }
  static member sortBars(data:series<string, float>) = 
    { YouGuessSortBars.data = data; maxValue = None; size = None, None }
  static member line(data:series<obj, float>) =
    { YouGuessLine.data = data; clip = None; 
      markerColor = None; guessLabel = None; topLabel = None; knownLabel = None; markers = None
      knownColor = None; unknownColor = None; drawColor = None; 
      options = ChartOptions.Default }

// ------------------------------------------------------------------------------------------------
// Compost Charts API
// ------------------------------------------------------------------------------------------------

type CompostBubblesChartSet =
  private 
    { data : series<obj, obj> 
      selectY : obj -> obj
      selectX : obj -> obj
      selectSize : option<obj -> obj>
      bubbleColor : string option
  // [copy-paste]
      options : ChartOptions }
  member y.setTitle(title) =
    { y with options = { y.options with title = title } }
  member y.setLegend(position) = 
    { y with options = { y.options with legend = { position = position } } }
  member y.setSize(?width, ?height) = 
    { y with options = { y.options with size = (orElse width (fst y.options.size), orElse height (snd y.options.size)) } }
  member y.setAxisX(?minValue, ?maxValue, ?label) = 
    let ax = { y.options.xAxis with minValue = orElse minValue y.options.xAxis.minValue; maxValue = orElse maxValue y.options.xAxis.maxValue; label = orElse label y.options.xAxis.label }
    { y with options = { y.options with xAxis = ax } }
  member y.setAxisY(?minValue, ?maxValue, ?label) = 
    let ax = { y.options.yAxis with minValue = orElse minValue y.options.yAxis.minValue; maxValue = orElse maxValue y.options.yAxis.maxValue; label = orElse label y.options.yAxis.label }
    { y with options = { y.options with yAxis = ax } }
  // [/copy-paste]
  member y.setColors(?bubbleColor) = 
    { y with bubbleColor = defaultArg bubbleColor y.bubbleColor }
  member y.show(outputId) = 
    InteractiveHelpers.showStaticApp outputId y.options.size y.data
      (fun data size -> 
        let ss = match y.selectSize with Some f -> (fun x -> Some(f x)) | _ -> (fun _ -> None)
        let data = data |> Array.map (fun (_, v) -> y.selectX v, y.selectY v, ss v)
        let bc = defaultArg y.bubbleColor "#20a030"
        Charts.renderBubbles y.options size bc data)

type CompostBubblesChart<'k, 'v>(data:series<'k, 'v>) = 
  member c.set(x:'v -> obj, y:'v -> obj, ?size:'v -> obj) = 
    { CompostBubblesChartSet.data = unbox data
      selectX = unbox x; selectY = unbox y
      selectSize = unbox size; bubbleColor = None 
      options = ChartOptions.Default }


type CompostColBarChart =
  private 
    { isBar : bool
      data : series<string, float>
      colors : string[] option
  // [copy-paste]
      options : ChartOptions }  
  member y.setTitle(title) =
    { y with options = { y.options with title = title } }
  member y.setLegend(position) = 
    { y with options = { y.options with legend = { position = position } } }
  member y.setSize(?width, ?height) = 
    { y with options = { y.options with size = (orElse width (fst y.options.size), orElse height (snd y.options.size)) } }
  member y.setAxisX(?minValue, ?maxValue, ?label) = 
    let ax = { y.options.xAxis with minValue = orElse minValue y.options.xAxis.minValue; maxValue = orElse maxValue y.options.xAxis.maxValue; label = orElse label y.options.xAxis.label }
    { y with options = { y.options with xAxis = ax } }
  member y.setAxisY(?minValue, ?maxValue, ?label) = 
    let ax = { y.options.yAxis with minValue = orElse minValue y.options.yAxis.minValue; maxValue = orElse maxValue y.options.yAxis.maxValue; label = orElse label y.options.yAxis.label }
    { y with options = { y.options with yAxis = ax } }
  // [/copy-paste]
  member y.setColors(?colors) = 
    { y with colors = defaultArg colors y.colors }
  member y.show(outputId) = 
    InteractiveHelpers.showStaticApp outputId y.options.size y.data
      (fun data size -> 
        let cc = defaultArg y.colors vega10
        Charts.renderColsBars y.isBar y.options size cc (Seq.map fst data) data)


type CompostLineChart =
  private 
    { data : series<obj, obj>
      lineColor : string option
  // [copy-paste]
      options : ChartOptions }  
  member y.setTitle(title) =
    { y with options = { y.options with title = title } }
  member y.setLegend(position) = 
    { y with options = { y.options with legend = { position = position } } }
  member y.setSize(?width, ?height) = 
    { y with options = { y.options with size = (orElse width (fst y.options.size), orElse height (snd y.options.size)) } }
  member y.setAxisX(?minValue, ?maxValue, ?label) = 
    let ax = { y.options.xAxis with minValue = orElse minValue y.options.xAxis.minValue; maxValue = orElse maxValue y.options.xAxis.maxValue; label = orElse label y.options.xAxis.label }
    { y with options = { y.options with xAxis = ax } }
  member y.setAxisY(?minValue, ?maxValue, ?label) = 
    let ax = { y.options.yAxis with minValue = orElse minValue y.options.yAxis.minValue; maxValue = orElse maxValue y.options.yAxis.maxValue; label = orElse label y.options.yAxis.label }
    { y with options = { y.options with yAxis = ax } }
  // [/copy-paste]
  member y.setColors(?lineColor) = 
    { y with lineColor = defaultArg lineColor y.lineColor }
  member y.show(outputId) = 
    InteractiveHelpers.showStaticApp outputId y.options.size y.data
      (fun data size -> 
        let lc = defaultArg y.lineColor "#1f77b4"
        Charts.renderLines y.options size [| lc |] ["Data"] [| data |])


type CompostLinesChart =
  private 
    { data : series<obj, obj>[]
      lineColors : string[] option
  // [copy-paste]
      options : ChartOptions }  
  member y.setTitle(title) =
    { y with options = { y.options with title = title } }
  member y.setLegend(position) = 
    { y with options = { y.options with legend = { position = position } } }
  member y.setSize(?width, ?height) = 
    { y with options = { y.options with size = (orElse width (fst y.options.size), orElse height (snd y.options.size)) } }
  member y.setAxisX(?minValue, ?maxValue, ?label) = 
    let ax = { y.options.xAxis with minValue = orElse minValue y.options.xAxis.minValue; maxValue = orElse maxValue y.options.xAxis.maxValue; label = orElse label y.options.xAxis.label }
    { y with options = { y.options with xAxis = ax } }
  member y.setAxisY(?minValue, ?maxValue, ?label) = 
    let ax = { y.options.yAxis with minValue = orElse minValue y.options.yAxis.minValue; maxValue = orElse maxValue y.options.yAxis.maxValue; label = orElse label y.options.yAxis.label }
    { y with options = { y.options with yAxis = ax } }
  // [/copy-paste]
  member y.setColors(?lineColors) = 
    { y with lineColors = defaultArg lineColors y.lineColors }
  member y.show(outputId) = Async.StartImmediate <| async {
    let! data = Async.Parallel [ for d in y.data -> Async.AwaitFuture d.data ]
    return! InteractiveHelpers.showStaticAppAsync outputId y.options.size
      (fun size -> 
        let lcs = defaultArg y.lineColors vega10
        Charts.renderLines y.options size lcs (y.data |> Seq.map (fun s -> s.seriesName)) data) }
      
type CompostCharts() = 
  member c.bubbles(data:series<'k, 'v>) = 
    CompostBubblesChart<'k, 'v>(data)
  member c.line(data:series<'k, 'v>) = 
    { CompostLineChart.data = unbox data; lineColor = None; options = ChartOptions.Default }
  member c.lines(data:series<'k, 'v>[]) = 
    { CompostLinesChart.data = unbox data; lineColors = None; options = ChartOptions.Default }
  member c.bar(data:series<string, float>) = 
    { CompostColBarChart.data = data; colors = None; options = ChartOptions.Default; isBar = true }
  member c.column(data:series<string, float>) = 
    { CompostColBarChart.data = data; colors = None; options = ChartOptions.Default; isBar = false }

type compost = 
  static member charts = CompostCharts()
