﻿#light

namespace VimCore.Modes.Normal
open VimCore
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open System.Windows.Input
open System.Windows.Media

type internal NormalMode( _bufferData : IVimBufferData ) = 
    let mutable _data = {
        VimBufferData=_bufferData; 
        RunFunc = (fun _ _ -> NormalModeResult.Complete);
        Count=1;
        Register=_bufferData.RegisterMap.DefaultRegister;
        LastSearch=None;
        WaitingForMoreInput=false;
    }
    let mutable _operationMap : Map<KeyInput,Operation> = Map.empty

    member this.TextView = _bufferData.TextView
    member this.TextBuffer = _bufferData.TextBuffer
    member this.VimHost = _bufferData.VimHost
    member this.CaretPoint = _bufferData.TextView.Caret.Position.BufferPosition
    member this.Settings = _bufferData.Settings
    
    /// Get the current search options based off of the stored data
    member this.CalculateSearchOptions = 
        if _bufferData.Settings.IgnoreCase then SearchOptions.IgnoreCase
        else SearchOptions.None

    /// Begin an incremental search.  Called when the user types / into the editor
    member this.BeginIncrementalSearch (kind:SearchKind) =
        let view = this.TextView
        let origCaretPos = view.Caret.Position
        let resetView () =
            view.Caret.MoveTo(origCaretPos.VirtualBufferPosition) |> ignore
            ViewUtil.ClearSelection view
        let doSearch pattern nextFunc : NormalModeResult =
            let search = new IncrementalSearch(pattern, kind, this.CalculateSearchOptions)
            let nextFunc = nextFunc search
            _bufferData.VimHost.UpdateStatus("/" + pattern)
            match search.FindNextMatch (view.Caret.Position.BufferPosition) with
                | Some span ->
                    view.Selection.Select(span,false) |> ignore
                    ViewUtil.MoveCaretToPoint view (span.Start) |> ignore
                    NeedMore2 (nextFunc)
                | None ->
                    resetView()
                    NeedMore2 (nextFunc)            
        let rec inner (search:IncrementalSearch) (d:NormalModeData) (ki:KeyInput) : NormalModeResult =
            match ki.Key with
                | Key.Escape ->
                    resetView()
                    NormalModeResult.Complete
                | Key.Enter ->
                    ViewUtil.ClearSelection view
                    NormalModeResult.SearchComplete search
                | Key.Back -> 
                    if search.Pattern.Length = 1 then
                        // User backspaced completely out of the search
                        resetView()
                        NormalModeResult.Complete
                    else 
                        let pattern = search.Pattern.Substring(0, search.Pattern.Length-1)
                        doSearch pattern inner
                | _ -> 
                    let c = ki.Char
                    let pattern = search.Pattern + (c.ToString())
                    doSearch pattern inner
        _bufferData.VimHost.UpdateStatus("/")
        NeedMore2 (inner (new IncrementalSearch(System.String.Empty, kind, this.CalculateSearchOptions)))
    
    /// Find the next match of the passed in incremental search
    member this.TryFindNextMatch (search:IncrementalSearch) (count:int) =    
        let view = _bufferData.TextView
        let current = view.Caret.Position.BufferPosition
        let next = 
            match SearchKindUtil.IsForward (search.SearchKind) with
                | true -> TssUtil.GetNextPointWithWrap current
                | false -> TssUtil.GetPreviousPointWithWrap current
        match search.FindNextMatch (next) with 
            | None -> false
            | Some (s) ->
                ViewUtil.MoveCaretToPoint view (s.Start) |> ignore
                match count with 
                    | 1 -> true
                    | _ -> this.TryFindNextMatch search (count-1)
                    
    member this.FindNextMatch (search:option<IncrementalSearch>) (count:int) =
        let host = _bufferData.VimHost
        match search with
            | None -> host.UpdateStatus("No previous search")
            | Some (s) ->
                match this.TryFindNextMatch s count with 
                    | false -> host.UpdateStatus("Pattern not found: " + s.Pattern)
                    | _ -> ()
        NormalModeResult.Complete
   
    member this.FindNextWordUnderCursor (count:int) (kind:SearchKind) =
        let view = _bufferData.TextView
        match TssUtil.FindCurrentFullWordSpan (ViewUtil.GetCaretPoint view) WordKind.NormalWord with
            | None ->
                _bufferData.VimHost.UpdateStatus("No word under cursor")
                NormalModeResult.Complete
            | Some (span) ->    
                // Search is regex based so use \W on boundaries to capture full word semantics
                let pattern = span.GetText()             
                let search = new IncrementalSearch(pattern, kind, this.CalculateSearchOptions)
                this.TryFindNextMatch search count |> ignore
                NormalModeResult.Complete
                
    /// Paste after the current cursor position.  Don't forget that a linewise paste
    /// operation needs to occur under the cursor
    member this.PasteAfter text opKind =
        let view = _bufferData.TextView
        let caretPoint = ViewUtil.GetCaretPoint view
        let buffer = view.TextBuffer
        match opKind with
        | OperationKind.LineWise ->
            let line = caretPoint.GetContainingLine()
            let span = new SnapshotSpan(line.EndIncludingLineBreak, 0)
            let tss = buffer.Replace(span.Span, text) 
            
            // For a LineWise paste we want to place the cursor at the start
            // of the next line
            let nextLine = tss.GetLineFromLineNumber(line.LineNumber+1)
            view.Caret.MoveTo(nextLine.Start) |> ignore
        | OperationKind.CharacterWise ->
            let point = TssUtil.GetNextPoint caretPoint
            let span = new SnapshotSpan(point,0)
            buffer.Replace(span.Span, text) |> ignore
        | _ -> failwith "Invalid Enum Value"
        NormalModeResult.Complete
        
    // Paste at the current cursor position
    member this.PasteBefore text = 
        let view = _bufferData.TextView
        let point = ViewUtil.GetCaretPoint view
        let span = new SnapshotSpan(point,0)
        let buffer = view.TextBuffer
        buffer.Replace(span.Span, text) |> ignore
        NormalModeResult.Complete
        
    member this.WaitForMotion ki (d:NormalModeData) doneFunc = 
        let rec f (result:MotionResult) = 
            match result with 
                | MotionResult.Complete (span) -> 
                    d.VimBufferData.VimHost.UpdateStatus(System.String.Empty)
                    doneFunc span
                | NeedMoreInput (moreFunc) ->
                    d.VimBufferData.VimHost.UpdateStatus("Waiting for motion")
                    let inputFunc _ ki = f (moreFunc ki)
                    NormalModeResult.NeedMore ({d with RunFunc = inputFunc })
                | InvalidMotion (msg,moreFunc) ->
                    _bufferData.VimHost.UpdateStatus(msg)
                    let inputFunc _ ki = f (moreFunc ki)
                    NormalModeResult.NeedMore ({d with RunFunc = inputFunc})
                | Error (msg) ->
                    d.VimBufferData.VimHost.UpdateStatus(msg)
                    NormalModeResult.Complete
                | Cancel -> 
                    d.VimBufferData.VimHost.UpdateStatus(System.String.Empty)
                    NormalModeResult.Complete
        f (MotionCapture.ProcessView d.VimBufferData.TextView ki d.Count)
        
        
    // Respond to the d command.  Need the finish motion
    member this.WaitDelete =
        let inner (d:NormalModeData) (ki:KeyInput) =
            match ki.Key with 
                | Key.D -> 
                    let point = ViewUtil.GetCaretPoint d.VimBufferData.TextView
                    let point = point.GetContainingLine().Start
                    let span = TssUtil.GetLineRangeSpanIncludingLineBreak point d.Count
                    let tuple = (span,  MotionKind.Inclusive, OperationKind.LineWise)
                    BufferUtil.DeleteSpan tuple d.Register |> ignore
                    NormalModeResult.Complete
                | _ -> 
                    let func tuple = 
                        BufferUtil.DeleteSpan tuple d.Register |> ignore
                        NormalModeResult.Complete
                    this.WaitForMotion ki d func
        inner
        
    // Respond to the y command.  Need to wait for a complete motion
    member this.WaitYank =
        let inner (d:NormalModeData) (ki:KeyInput) =
            match ki.Key with 
                | Key.Y -> 
                    let point = ViewUtil.GetCaretPoint d.VimBufferData.TextView
                    let point = point.GetContainingLine().Start
                    let span = TssUtil.GetLineRangeSpanIncludingLineBreak point d.Count
                    let regValue = {Value=span.GetText();MotionKind = MotionKind.Inclusive; OperationKind = OperationKind.LineWise};
                    d.Register.UpdateValue (regValue)
                    NormalModeResult.Complete
                | _ ->
                    let inner (ss:SnapshotSpan,motionKind,opKind) = 
                        let regValue = {Value=ss.GetText();MotionKind=motionKind;OperationKind=opKind}
                        d.Register.UpdateValue(regValue)
                        NormalModeResult.Complete
                    this.WaitForMotion ki d inner
        inner 
        
    /// Implement the < operator        
    member this.ShiftLeft =
        let inner (d:NormalModeData) (ki:KeyInput) =
            match ki.Char with 
                | '<' ->
                    let tss = this.CaretPoint.Snapshot
                    let startLine = this.CaretPoint.GetContainingLine()
                    let endLineNumber = min (tss.LineCount-1) (startLine.LineNumber+d.Count-1)
                    let span = new SnapshotSpan(startLine.Start, tss.GetLineFromLineNumber(endLineNumber).End)
                    BufferUtil.ShiftLeft span _bufferData.Settings.ShiftWidth |> ignore
                    NormalModeResult.Complete
                | _ ->
                    let inner2 (span:SnapshotSpan,_,_) =
                        BufferUtil.ShiftLeft span _bufferData.Settings.ShiftWidth |> ignore                                          
                        NormalModeResult.Complete
                    this.WaitForMotion ki d inner2
        inner                                            
                    
    /// Implements the > operator.  Check for the special > motion key and then default
    /// back to a standard motion                    
    member this.ShiftRight =
        let inner (d:NormalModeData) (ki:KeyInput) =
            match ki.Char with
                | '>' ->
                    let tss = this.CaretPoint.Snapshot
                    let startLine = this.CaretPoint.GetContainingLine()
                    let endLineNumber = min (tss.LineCount-1) (startLine.LineNumber+d.Count-1)
                    let span = new SnapshotSpan(startLine.Start, tss.GetLineFromLineNumber(endLineNumber).End)
                    BufferUtil.ShiftRight span _bufferData.Settings.ShiftWidth |> ignore
                    NormalModeResult.Complete
                | _ ->
                    let inner2 (span:SnapshotSpan,_,_) =
                        BufferUtil.ShiftRight span _bufferData.Settings.ShiftWidth |> ignore
                        NormalModeResult.Complete
                    this.WaitForMotion ki d inner2
        inner
                            
    member this.MoveEndOfLine (d:NormalModeData) =
        ViewUtil.MoveCaretToEndOfLine d.VimBufferData.TextView |> ignore
        NormalModeResult.Complete
    
    member this.MoveStartOfLine (d:NormalModeData) =
        ViewUtil.MoveCaretToBeginingOfLine d.VimBufferData.TextView |> ignore
        NormalModeResult.Complete
    
    /// Move the caret as a result of the user hitting enter.  The caret should jump to the
    /// start of the next line unless we are at the end of the buffer
    member this.MoveForEnter (view:ITextView) (host:IVimHost)=
        let tss = view.TextSnapshot
        let point = ViewUtil.GetCaretPoint view
        let last = TssUtil.GetLastLine tss
        match last.LineNumber = point.GetContainingLine().LineNumber with
            | false -> 
                let next = tss.GetLineFromLineNumber(point.GetContainingLine().LineNumber+1)
                ViewUtil.MoveCaretToPoint view (next.Start) |> ignore
                NormalModeResult.Complete
            | true ->
                host.Beep()
                NormalModeResult.Complete
        

    /// Add a line below the current cursor position and switch back to insert mode
    member this.AddLineBelow (view:ITextView) =
    
        // Insert the line
        let point = ViewUtil.GetCaretPoint view
        let line = point.GetContainingLine()
        let newLine = BufferUtil.AddLineBelow line
        
        // Move the caret to the same indent position as the previous line
        let indent = TssUtil.FindIndentPosition(line)
        let point = new VirtualSnapshotPoint(newLine, indent)
        ViewUtil.MoveCaretToVirtualPoint view point |> ignore
        NormalModeResult.SwitchMode (ModeKind.Insert)
        
    member this.DeleteCharacterUnderCursor (d:NormalModeData) =
        let view = d.VimBufferData.TextView
        let range = TssUtil.GetCharacterSpan(view.Caret.Position.BufferPosition)
        let tuple = (range, MotionKind.Exclusive, OperationKind.CharacterWise)
        BufferUtil.DeleteSpan tuple d.Register |> ignore
        NormalModeResult.Complete

    /// Delete the character range before the current cursor.  Used to implement the X
    /// command. 
    member this.DeleteCharacterBeforeCursor (view:ITextView) count r =
        let caretPoint = ViewUtil.GetCaretPoint view
        let range = TssUtil.GetReverseCharacterSpan caretPoint count
        let tuple = (range, MotionKind.Exclusive, OperationKind.CharacterWise)
        BufferUtil.DeleteSpan tuple r |> ignore
        NormalModeResult.Complete

    /// Core method for scrolling the editor up or down
    member this.ScrollCore dir count =
        let lines = VimSettingsUtil.GetScrollLineCount this.Settings this.TextView
        let tss = this.TextBuffer.CurrentSnapshot
        let curLine = this.CaretPoint.GetContainingLine().LineNumber
        let newLine = 
            match dir with
            | ScrollDirection.Down -> min (tss.LineCount - 1) (curLine + lines)
            | ScrollDirection.Up -> max (0) (curLine - lines)
            | _ -> failwith "Invalid enum value"
        let newCaret = tss.GetLineFromLineNumber(newLine).Start
        ViewUtil.ClearSelection this.TextView
        this.TextView.Caret.MoveTo(newCaret) |> ignore
        this.TextView.Caret.EnsureVisible()
        NormalModeResult.Complete

    /// Complete the specified motion function            
    member this.MotionFunc view count func =
        let rec runCount count =
            func view |> ignore
            match count with
                | 1 -> NormalModeResult.Complete
                | _ -> runCount (count-1) 
        ViewUtil.ClearSelection view                
        runCount count

    member this.BuildOperationsMap = 
        let l : list<Operation> = [
            {   KeyInput=InputUtil.CharToKeyInput('h'); 
                RunFunc=(fun d -> this.MotionFunc this.TextView d.Count (ViewUtil.MoveCaretLeft) ) };
            {   KeyInput=InputUtil.CharToKeyInput('j');
                RunFunc=(fun d -> this.MotionFunc this.TextView d.Count (ViewUtil.MoveCaretDown)) };
            {   KeyInput=InputUtil.CharToKeyInput('k');
                RunFunc=(fun d -> this.MotionFunc this.TextView d.Count (ViewUtil.MoveCaretUp)) };
            {   KeyInput=InputUtil.CharToKeyInput('l');
                RunFunc=(fun d -> this.MotionFunc this.TextView d.Count (ViewUtil.MoveCaretRight)) };
            {   KeyInput=InputUtil.CharToKeyInput('w');
                RunFunc=(fun d -> this.MotionFunc this.TextView d.Count (fun v -> ViewUtil.MoveWordForward v WordKind.NormalWord)) };
            {   KeyInput=InputUtil.CharToKeyInput('b');
                RunFunc=(fun d -> this.MotionFunc this.TextView d.Count (fun v -> ViewUtil.MoveWordBackward v WordKind.NormalWord)) };
            {   KeyInput=InputUtil.CharToKeyInput('W');
                RunFunc=(fun d -> this.MotionFunc this.TextView d.Count (fun v -> ViewUtil.MoveWordForward v WordKind.BigWord)) };
            {   KeyInput=InputUtil.CharToKeyInput('B');
                RunFunc=(fun d -> this.MotionFunc this.TextView d.Count (fun v -> ViewUtil.MoveWordBackward v WordKind.BigWord)) };
            {   KeyInput=InputUtil.CharToKeyInput('x');
                RunFunc=(fun d -> this.DeleteCharacterUnderCursor d) };
            {   KeyInput=InputUtil.CharToKeyInput('X');
                RunFunc=(fun d -> this.DeleteCharacterBeforeCursor this.TextView d.Count d.Register) };
            {   KeyInput=InputUtil.CharToKeyInput('d');
                RunFunc=(fun d -> NeedMore2(this.WaitDelete)) };
            {   KeyInput=InputUtil.CharToKeyInput('y');
                RunFunc=(fun d -> NeedMore2(this.WaitYank)); };
            {   KeyInput=InputUtil.CharToKeyInput('<');
                RunFunc=(fun d -> NeedMore2(this.ShiftLeft)); };
            {   KeyInput=InputUtil.CharToKeyInput('>');
                RunFunc=(fun d -> NeedMore2(this.ShiftRight)); };
            {   KeyInput=InputUtil.CharToKeyInput('p');
                RunFunc=(fun d -> this.PasteAfter (StringUtil.Repeat (d.Register.StringValue) (d.Count)) d.Register.Value.OperationKind) };
            {   KeyInput=InputUtil.CharToKeyInput('P');
                RunFunc=(fun d -> this.PasteBefore (StringUtil.Repeat (d.Register.StringValue) (d.Count))) };
            {   KeyInput=InputUtil.CharToKeyInput('$');
                RunFunc=(fun d -> this.MoveEndOfLine d) };
            {   KeyInput=InputUtil.CharToKeyInput('^');
                RunFunc=(fun d -> this.MoveStartOfLine d) };
            {   KeyInput=InputUtil.CharToKeyInput('/');
                RunFunc=(fun d -> this.BeginIncrementalSearch SearchKind.ForwardWithWrap) };
            {   KeyInput=InputUtil.CharToKeyInput('?');
                RunFunc=(fun d -> this.BeginIncrementalSearch SearchKind.BackwardWithWrap) };
            {   KeyInput=InputUtil.CharToKeyInput('n');
                RunFunc=(fun d -> this.FindNextMatch d.LastSearch d.Count) };
            {   KeyInput=InputUtil.CharToKeyInput('*');
                RunFunc=(fun d -> this.FindNextWordUnderCursor d.Count SearchKind.ForwardWithWrap) };
            {   KeyInput=InputUtil.CharToKeyInput('#');
                RunFunc=(fun d -> this.FindNextWordUnderCursor d.Count SearchKind.BackwardWithWrap) };
            {   KeyInput=InputUtil.CharToKeyInput('i');
                RunFunc=(fun _ -> NormalModeResult.SwitchMode (ModeKind.Insert)) };
            {   KeyInput=InputUtil.CharToKeyInput(':');
                RunFunc=(fun _ -> NormalModeResult.SwitchMode (ModeKind.Command)); };
            {   KeyInput=InputUtil.CharToKeyInput('A');
                RunFunc=(fun d ->
                    ViewUtil.MoveCaretToEndOfLine (d.VimBufferData.TextView) |> ignore  
                    NormalModeResult.SwitchMode (ModeKind.Insert)) };
            {   KeyInput=InputUtil.CharToKeyInput('u');
                RunFunc=(fun d -> 
                    this.VimHost.Undo this.TextBuffer d.Count
                    NormalModeResult.Complete) };
            {   KeyInput=InputUtil.CharToKeyInput('o');
                RunFunc=(fun d -> this.AddLineBelow this.TextView) };
            {   KeyInput=InputUtil.KeyToKeyInput(Key.Enter);
                RunFunc=(fun d -> this.MoveForEnter this.TextView d.VimBufferData.VimHost) };
            {   KeyInput=KeyInput('u', Key.U, ModifierKeys.Control);
                RunFunc=(fun d -> this.ScrollCore ScrollDirection.Up d.Count) };
            {   KeyInput=KeyInput('d', Key.D, ModifierKeys.Control);
                RunFunc=(fun d -> this.ScrollCore ScrollDirection.Down d.Count) };
            {   KeyInput=KeyInput('J', Key.J, ModifierKeys.Shift);
                RunFunc=(fun d -> 
                    let start = ViewUtil.GetCaretPoint this.TextView
                    let kind = VimCore.Modes.Common.JoinKind.RemoveEmptySpaces
                    let res = VimCore.Modes.Common.Operations.Join this.TextView start kind d.Count
                    if not res then
                        this.VimHost.Beep()
                    NormalModeResult.Complete) };
            {   KeyInput=KeyInput(']', Key.OemCloseBrackets, ModifierKeys.Control);
                RunFunc=(fun d ->
                    match VimCore.Modes.Common.Operations.GoToDefinition this.TextView this.VimHost with
                    | VimCore.Modes.Common.Operations.Succeeded -> ()
                    | VimCore.Modes.Common.Operations.Failed(msg) ->
                        this.VimHost.UpdateStatus(msg)
                        ()
                    NormalModeResult.Complete) };
            {   KeyInput=InputUtil.CharToKeyInput('m');
                RunFunc=Operations.Mark };
            {   KeyInput=InputUtil.CharToKeyInput('\'');
                RunFunc=Operations.JumpToMark };
            {   KeyInput=InputUtil.CharToKeyInput('`');
                RunFunc=Operations.JumpToMark };
            ]
        l |> List.map (fun d -> d.KeyInput,d) |> Map.ofList

   
    /// Repsonible for getting the count                
    member this.GetCount (d:NormalModeData) (ki:KeyInput) = 
        let rec inner ki (func: KeyInput -> CountResult) = 
            match func ki with
                | CountResult.Complete (count,nextKi) ->
                    CountComplete(count, nextKi)
                | CountResult.NeedMore(f) ->
                    let nextFunc _ ki2 = inner ki2 f
                    NeedMore2(nextFunc)
        inner ki (CountCapture.Process)                    
        
    /// Responsible for getting the register 
    member this.GetRegister (m:IRegisterMap) (ki:KeyInput) =
        let c = InputUtil.KeyInputToChar ki
        let reg = m.GetRegister c
        RegisterComplete(reg)

    member this.StartCore (d:NormalModeData) (ki:KeyInput) =
        if ki.IsDigit then this.GetCount d ki
        elif ki.Key = Key.OemQuotes && ki.ModifierKeys = ModifierKeys.Shift then 
            let f (d:NormalModeData) ki = this.GetRegister (d.VimBufferData.RegisterMap) ki
            NeedMore2(f)
        else
            match Map.tryFind ki _operationMap with
            | Some op -> op.RunFunc(d)
            | None -> 
                this.VimHost.Beep()
                NormalModeResult.Complete

    /// Reset the internal data for the NormalMode instance
    member this.ResetData = 
        _data <- {
            _data with 
                Count=1; 
                RunFunc=this.StartCore;
                WaitingForMoreInput=false;
                Register=_bufferData.RegisterMap.DefaultRegister }
        if _operationMap.Count = 0 then
            _operationMap <- this.BuildOperationsMap


    member this.ChangeLastSearch search = _data <- { _data with LastSearch = Some search }
    member this.Register = _data.Register
    member this.Count = _data.Count
    
    interface IMode with 
        member this.Commands = 
            _operationMap
                |> Map.toSeq
                |> Seq.map (fun (k,v) -> k)

        member this.ModeKind = ModeKind.Normal
        member this.CanProcess (ki:KeyInput) =
            if _data.WaitingForMoreInput then
                true
            else if ki.IsDigit then
                true
            else
                _operationMap.ContainsKey ki

        member this.Process ki = 
            match _data.RunFunc _data ki with
                | NormalModeResult.Complete -> 
                    this.ResetData
                    Processed
                | NormalModeResult.NeedMore(d) -> 
                    _data <- { d with WaitingForMoreInput=true }
                    Processed
                | NeedMore2(f) ->
                    _data <- {_data with RunFunc=f;WaitingForMoreInput=true}
                    Processed
                | SearchComplete(search) -> 
                    this.ResetData
                    _data <- {_data with LastSearch=(Some search) }
                    Processed
                | NormalModeResult.SwitchMode (kind) -> 
                    this.ResetData // Make sure to reset information when switching modes
                    ProcessResult.SwitchMode kind
                | CountComplete (count,nextKi) ->
                    _data <- {_data with Count=count; RunFunc=this.StartCore}
                    (this :> IMode).Process nextKi
                | RegisterComplete (reg) ->     
                    _data <- {_data with Register=reg; RunFunc=this.StartCore;  WaitingForMoreInput=false }
                    Processed
        member this.OnEnter ()  =
            _bufferData.BlockCaret.Show()
            this.ResetData
        member this.OnLeave () = 
            _bufferData.BlockCaret.Hide()
    

