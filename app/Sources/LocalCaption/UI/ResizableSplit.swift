import SwiftUI
import AppKit

/// Two panes with a draggable divider (owner, 2026-10-02: the captions/answers border is
/// adjustable). `fraction` is the first pane's share of the space, kept by the caller (persisted
/// with `@AppStorage`); each pane keeps its minimum size. Double-click the divider to reset.
struct ResizableSplit<First: View, Second: View>: View {
    let axis: Axis
    @Binding var fraction: Double
    let defaultFraction: Double
    let minFirst: CGFloat
    let minSecond: CGFloat
    @ViewBuilder var first: First
    @ViewBuilder var second: Second

    @State private var dragStart: Double?
    private let handle: CGFloat = 9

    var body: some View {
        GeometryReader { geo in
            let total = axis == .horizontal ? geo.size.width : geo.size.height
            let avail = max(1, total - handle)
            let size = firstSize(avail)
            let stack = axis == .horizontal ? AnyLayout(HStackLayout(spacing: 0)) : AnyLayout(VStackLayout(spacing: 0))
            stack {
                first.frame(width: axis == .horizontal ? size : nil, height: axis == .vertical ? size : nil)
                divider(avail)
                second.frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .coordinateSpace(name: "split")
    }

    /// The first pane's size: the stored share, held inside both minimums when there's room.
    private func firstSize(_ avail: CGFloat) -> CGFloat {
        let wanted = CGFloat(fraction) * avail
        let lo = minFirst, hi = avail - minSecond
        return hi >= lo ? min(max(wanted, lo), hi) : wanted
    }

    private func divider(_ avail: CGFloat) -> some View {
        ZStack {
            Color.clear
            if axis == .horizontal {
                Rectangle().fill(Color(nsColor: .separatorColor)).frame(width: 1)
            } else {
                Rectangle().fill(Color(nsColor: .separatorColor)).frame(height: 1)
            }
        }
        .frame(width: axis == .horizontal ? handle : nil, height: axis == .vertical ? handle : nil)
        .contentShape(Rectangle())
        .onHover { inside in
            if inside { (axis == .horizontal ? NSCursor.resizeLeftRight : NSCursor.resizeUpDown).push() }
            else { NSCursor.pop() }
        }
        .gesture(
            DragGesture(minimumDistance: 1, coordinateSpace: .named("split"))
                .onChanged { value in
                    let start = dragStart ?? fraction
                    dragStart = start
                    let delta = axis == .horizontal ? value.translation.width : value.translation.height
                    fraction = min(1, max(0, start + Double(delta / avail)))
                }
                .onEnded { _ in
                    dragStart = nil
                    fraction = Double(firstSize(avail) / avail)   // store what's actually shown
                }
        )
        .onTapGesture(count: 2) { fraction = defaultFraction }
        .help("Drag to resize · double-click to reset")
        .accessibilityLabel("Resize captions and answers")
    }
}
