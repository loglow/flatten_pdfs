import AppKit
import PDFKit
import CoreGraphics
import UniformTypeIdentifiers

// Requires macOS 11.0 or later (UTType / allowedContentTypes).

// shared/app-spec.json is the single source of truth for the user-facing
// strings, layout metrics (in points), and version shared with the Windows
// app. The build script copies it into the bundle's Resources and stamps the
// version into Info.plist; the spec is decoded once at launch. Each app
// declares only the fields it uses — unknown JSON keys are ignored.
struct Spec: Decodable {
    let name: String
    let version: String
    let strings: Strings
    let layout: Layout

    struct Strings: Decodable {
        let dropTitle: String
        let dropDetail: String
        let selectPdfsButton: String
        let clearLogButton: String
        let noPdfsSelected: String
        let unchangedMessage: String
        let finishingBeforeQuit: String
        let errorNotPdf: String
        let errorCannotOpen: String
        let errorLocked: String
        let errorNoPages: String
        let errorCannotCreateOutput: String
        let errorPageReadFailed: String
        let errorValidationFailed: String
    }

    struct Layout: Decodable {
        let windowWidth: CGFloat
        let windowHeight: CGFloat
        let minWindowWidth: CGFloat
        let minWindowHeight: CGFloat
        let padding: CGFloat
        let spacing: CGFloat
        let spacingAfterTitle: CGFloat
        let spacingAfterDetail: CGFloat
        let spacingAfterButtons: CGFloat
        let buttonGap: CGFloat
        let titleFontSize: CGFloat
        let detailFontSize: CGFloat
        let logFontSize: CGFloat
        let detailMaxWidth: CGFloat
        let logMinHeight: CGFloat
        let dropOutlineWidth: CGFloat
    }
}

let spec: Spec = {
    guard let url = Bundle.main.url(forResource: "app-spec", withExtension: "json"),
          let data = try? Data(contentsOf: url),
          let decoded = try? JSONDecoder().decode(Spec.self, from: data) else {
        fatalError("app-spec.json is missing from the app bundle or invalid")
    }
    return decoded
}()

private enum FlattenFailure: LocalizedError {
    case notPDF
    case cannotOpen
    case locked
    case noPages
    case cannotCreateOutput
    case pageReadFailed(Int)
    case outputValidationFailed

    var errorDescription: String? {
        switch self {
        case .notPDF:
            return spec.strings.errorNotPdf
        case .cannotOpen:
            return spec.strings.errorCannotOpen
        case .locked:
            return spec.strings.errorLocked
        case .noPages:
            return spec.strings.errorNoPages
        case .cannotCreateOutput:
            return spec.strings.errorCannotCreateOutput
        case .pageReadFailed(let pageNumber):
            return spec.strings.errorPageReadFailed
                .replacingOccurrences(of: "{n}", with: String(pageNumber))
        case .outputValidationFailed:
            return spec.strings.errorValidationFailed
        }
    }
}

private struct FlattenResult {
    let pageCount: Int
    let flattenedAnnotationCount: Int
    let removedLinkCount: Int
    let changed: Bool
}

extension URL {
    /// Single source of truth for "is this a PDF?". Prefers the file's actual
    /// content type (so a PDF with a wrong or missing extension still
    /// qualifies) and falls back to the extension when the resource value is
    /// unavailable (e.g. the file does not exist yet or cannot be stat'ed).
    fileprivate var isPDFFile: Bool {
        if let type = try? resourceValues(forKeys: [.contentTypeKey]).contentType {
            return type.conforms(to: .pdf)
        }
        return pathExtension.lowercased() == "pdf"
    }
}

private final class PDFFlattener {
    private let fileManager = FileManager.default

    /// Annotation subtypes that draw no page content of their own. A document
    /// whose only annotations are these gains nothing visible from flattening
    /// — and would lose its hyperlinks — so they do not trigger a rewrite.
    /// ("Popup" is the companion window of a note, not content in itself.)
    private static let nonContentAnnotationTypes: Set<String> = ["Link", "Popup"]

    /// Maps a /Rotate value onto 0..<360 so that equivalent rotations
    /// (e.g. -90 and 270) compare equal.
    private static func normalizedRotation(_ degrees: Int) -> Int {
        let remainder = degrees % 360
        return remainder < 0 ? remainder + 360 : remainder
    }

    func flattenInPlace(_ url: URL) throws -> FlattenResult {
        let accessed = url.startAccessingSecurityScopedResource()
        defer {
            if accessed {
                url.stopAccessingSecurityScopedResource()
            }
        }

        guard url.isPDFFile else {
            throw FlattenFailure.notPDF
        }

        guard let document = PDFDocument(url: url) else {
            throw FlattenFailure.cannotOpen
        }
        if document.isLocked {
            throw FlattenFailure.locked
        }
        guard document.pageCount > 0 else {
            throw FlattenFailure.noPages
        }

        var flattenableCount = 0
        var linkCount = 0
        var pageRotations: [Int] = []
        pageRotations.reserveCapacity(document.pageCount)
        for index in 0..<document.pageCount {
            guard let page = document.page(at: index) else {
                throw FlattenFailure.pageReadFailed(index + 1)
            }
            pageRotations.append(page.rotation)
            for annotation in page.annotations {
                if let type = annotation.type,
                   Self.nonContentAnnotationTypes.contains(type) {
                    if type == "Link" {
                        linkCount += 1
                    }
                } else {
                    flattenableCount += 1
                }
            }
        }

        // Do not rewrite a file that has nothing visible to flatten. This also
        // protects documents whose only annotations are hyperlinks, which a
        // re-render would silently destroy.
        if flattenableCount == 0 {
            return FlattenResult(pageCount: document.pageCount,
                                 flattenedAnnotationCount: 0,
                                 removedLinkCount: 0,
                                 changed: false)
        }

        // NSItemReplacementDirectory is the blessed location for staging an
        // atomic replaceItemAt: it is guaranteed to be on the same volume as
        // the target and, unlike a dotfile beside the original, it works under
        // the App Sandbox (a dropped file grants access to that file only, not
        // to its parent directory).
        let replacementDirectory: URL
        do {
            replacementDirectory = try fileManager.url(for: .itemReplacementDirectory,
                                                       in: .userDomainMask,
                                                       appropriateFor: url,
                                                       create: true)
        } catch {
            throw FlattenFailure.cannotCreateOutput
        }
        defer {
            try? fileManager.removeItem(at: replacementDirectory)
        }

        let temporaryURL = replacementDirectory.appendingPathComponent(
            "FlattenPDFs-\(UUID().uuidString).pdf"
        )

        guard let consumer = CGDataConsumer(url: temporaryURL as CFURL) else {
            throw FlattenFailure.cannotCreateOutput
        }

        // Carry document-level metadata across the re-render; CGContext does
        // not copy it automatically. (The outline/bookmarks and hyperlinks
        // cannot survive a render-based flatten.)
        var auxiliaryInfo: [CFString: Any] = [:]
        if let attributes = document.documentAttributes {
            if let title = attributes[PDFDocumentAttribute.titleAttribute] as? String {
                auxiliaryInfo[kCGPDFContextTitle] = title
            }
            if let author = attributes[PDFDocumentAttribute.authorAttribute] as? String {
                auxiliaryInfo[kCGPDFContextAuthor] = author
            }
            if let subject = attributes[PDFDocumentAttribute.subjectAttribute] as? String {
                auxiliaryInfo[kCGPDFContextSubject] = subject
            }
            if let creator = attributes[PDFDocumentAttribute.creatorAttribute] as? String {
                auxiliaryInfo[kCGPDFContextCreator] = creator
            }
            if let keywords = attributes[PDFDocumentAttribute.keywordsAttribute] {
                // PDFKit may hand back a string or an array of strings; CG
                // accepts either for kCGPDFContextKeywords.
                auxiliaryInfo[kCGPDFContextKeywords] = keywords
            }
        }

        var initialMediaBox = CGRect(x: 0, y: 0, width: 612, height: 792)
        guard let context = CGContext(consumer: consumer,
                                      mediaBox: &initialMediaBox,
                                      auxiliaryInfo.isEmpty
                                          ? nil
                                          : auxiliaryInfo as CFDictionary) else {
            throw FlattenFailure.cannotCreateOutput
        }

        for index in 0..<document.pageCount {
            try autoreleasepool {
                guard let page = document.page(at: index) else {
                    throw FlattenFailure.pageReadFailed(index + 1)
                }

                // Render with /Rotate temporarily zeroed so PDFKit draws raw,
                // unrotated page space that exactly fits the unrotated media
                // box. (Otherwise draw(with:to:) applies the rotation and the
                // content is clipped by the unrotated output box.) The
                // original rotation is reapplied to the output file after
                // closePDF(), since Core Graphics cannot write /Rotate itself.
                let previousRotation = page.rotation
                let previousDisplaySetting = page.displaysAnnotations
                page.rotation = 0
                page.displaysAnnotations = true
                defer {
                    page.rotation = previousRotation
                    page.displaysAnnotations = previousDisplaySetting
                }

                let sourceBounds = page.bounds(for: .mediaBox)
                var outputBounds = CGRect(x: 0,
                                          y: 0,
                                          width: max(sourceBounds.width, 1),
                                          height: max(sourceBounds.height, 1))
                let boxData = NSData(bytes: &outputBounds,
                                     length: MemoryLayout<CGRect>.size)
                let pageInfo: [String: Any] = [
                    kCGPDFContextMediaBox as String: boxData
                ]

                context.beginPDFPage(pageInfo as CFDictionary)
                context.saveGState()
                context.translateBy(x: -sourceBounds.minX,
                                    y: -sourceBounds.minY)

                // PDFKit renders the page and its visible annotations into the
                // graphics context. Because the destination is a new PDF page,
                // the annotation appearance becomes ordinary page content.
                page.draw(with: .mediaBox, to: context)

                context.restoreGState()
                context.endPDFPage()
            }
        }
        context.closePDF()

        // Core Graphics offers no way to write a page's /Rotate entry, so the
        // original rotations are reapplied in a PDFKit pass over the freshly
        // written file. Skipped entirely when no source page was rotated, to
        // avoid an unnecessary second serialization.
        if pageRotations.contains(where: { Self.normalizedRotation($0) != 0 }) {
            guard let rotationDocument = PDFDocument(url: temporaryURL),
                  rotationDocument.pageCount == pageRotations.count else {
                throw FlattenFailure.outputValidationFailed
            }
            for (index, rotation) in pageRotations.enumerated()
            where Self.normalizedRotation(rotation) != 0 {
                guard let page = rotationDocument.page(at: index) else {
                    throw FlattenFailure.outputValidationFailed
                }
                page.rotation = rotation
            }
            guard rotationDocument.write(to: temporaryURL) else {
                throw FlattenFailure.cannotCreateOutput
            }
        }

        guard let outputDocument = PDFDocument(url: temporaryURL),
              !outputDocument.isLocked,
              outputDocument.pageCount == document.pageCount else {
            throw FlattenFailure.outputValidationFailed
        }

        // A pure Core Graphics render cannot emit annotations, so this check
        // is nearly vacuous today — but it is cheap and defends against future
        // changes to the pipeline.
        var remainingAnnotations = 0
        for index in 0..<outputDocument.pageCount {
            guard let page = outputDocument.page(at: index) else {
                throw FlattenFailure.outputValidationFailed
            }
            remainingAnnotations += page.annotations.count
            // The flattened page must display with the same orientation as
            // the source page.
            guard Self.normalizedRotation(page.rotation)
                    == Self.normalizedRotation(pageRotations[index]) else {
                throw FlattenFailure.outputValidationFailed
            }
        }
        guard remainingAnnotations == 0 else {
            throw FlattenFailure.outputValidationFailed
        }

        // replaceItemAt performs the final swap only after the new PDF has been
        // completely written and checked, avoiding a half-written original.
        _ = try fileManager.replaceItemAt(url,
                                          withItemAt: temporaryURL,
                                          backupItemName: nil,
                                          options: [])

        return FlattenResult(pageCount: document.pageCount,
                             flattenedAnnotationCount: flattenableCount,
                             removedLinkCount: linkCount,
                             changed: true)
    }
}

private final class DropView: NSView {
    var onFiles: (([URL]) -> Void)?

    private let titleLabel = NSTextField(labelWithString: spec.strings.dropTitle)
    private let detailLabel = NSTextField(labelWithString: spec.strings.dropDetail)
    private let chooseButton = NSButton(title: spec.strings.selectPdfsButton, target: nil, action: nil)
    private let clearLogButton = NSButton(title: spec.strings.clearLogButton, target: nil, action: nil)
    // scrollableTextView() wires up the sizing plumbing a text view needs
    // inside a scroll view (resizability, width tracking, autoresizing mask)
    // that a bare NSTextView assigned as documentView does not get.
    private let logScrollView = NSTextView.scrollableTextView()
    private var logView: NSTextView {
        // scrollableTextView() always installs an NSTextView document view.
        logScrollView.documentView as! NSTextView
    }
    private static let logFont = NSFont.monospacedSystemFont(ofSize: spec.layout.logFontSize,
                                                             weight: .regular)

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        setup()
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        setup()
    }

    private func setup() {
        registerForDraggedTypes([.fileURL])

        // The layer is only used for the drop highlight; with no background
        // color of its own, the standard window background shows through and
        // tracks the system's light and dark appearance automatically.
        wantsLayer = true

        titleLabel.font = NSFont.systemFont(ofSize: spec.layout.titleFontSize, weight: .semibold)
        titleLabel.alignment = .center

        detailLabel.font = NSFont.systemFont(ofSize: spec.layout.detailFontSize)
        detailLabel.textColor = .secondaryLabelColor
        detailLabel.alignment = .center
        detailLabel.maximumNumberOfLines = 2
        detailLabel.lineBreakMode = .byWordWrapping

        chooseButton.target = self
        chooseButton.action = #selector(chooseFiles)
        chooseButton.bezelStyle = .rounded
        chooseButton.refusesFirstResponder = true

        clearLogButton.target = self
        clearLogButton.action = #selector(clearLog)
        clearLogButton.bezelStyle = .rounded
        clearLogButton.refusesFirstResponder = true

        logView.isEditable = false
        logView.isSelectable = true
        logView.font = Self.logFont
        logView.textContainerInset = NSSize(width: 8, height: 8)

        logScrollView.hasVerticalScroller = true
        logScrollView.borderType = .bezelBorder
        logScrollView.translatesAutoresizingMaskIntoConstraints = false

        let buttonStack = NSStackView(views: [chooseButton, clearLogButton])
        buttonStack.orientation = .horizontal
        buttonStack.alignment = .centerY
        buttonStack.spacing = spec.layout.buttonGap

        let stack = NSStackView(views: [titleLabel,
                                        detailLabel,
                                        buttonStack,
                                        logScrollView])
        stack.orientation = .vertical
        stack.alignment = .centerX
        stack.spacing = spec.layout.spacing
        stack.setCustomSpacing(spec.layout.spacingAfterTitle, after: titleLabel)
        stack.setCustomSpacing(spec.layout.spacingAfterDetail, after: detailLabel)
        stack.setCustomSpacing(spec.layout.spacingAfterButtons, after: buttonStack)
        stack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(stack)

        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: leadingAnchor, constant: spec.layout.padding),
            stack.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -spec.layout.padding),
            stack.topAnchor.constraint(equalTo: topAnchor, constant: spec.layout.padding),
            stack.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -spec.layout.padding),
            detailLabel.widthAnchor.constraint(lessThanOrEqualToConstant: spec.layout.detailMaxWidth),
            logScrollView.widthAnchor.constraint(equalTo: stack.widthAnchor),
            logScrollView.heightAnchor.constraint(greaterThanOrEqualToConstant: spec.layout.logMinHeight)
        ])
    }

    @objc func chooseFiles() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.pdf]
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        panel.begin { [weak self] response in
            guard response == .OK else { return }
            self?.onFiles?(panel.urls)
        }
    }

    @objc private func clearLog() {
        logView.string = ""
    }

    private func PDFURLs(from draggingInfo: NSDraggingInfo) -> [URL] {
        let objects = draggingInfo.draggingPasteboard.readObjects(
            forClasses: [NSURL.self],
            options: [.urlReadingFileURLsOnly: true]
        ) as? [URL] ?? []
        return objects.filter { $0.isPDFFile }
    }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        let valid = !PDFURLs(from: sender).isEmpty
        setHighlighted(valid)
        return valid ? .copy : []
    }

    override func draggingExited(_ sender: NSDraggingInfo?) {
        setHighlighted(false)
    }

    override func prepareForDragOperation(_ sender: NSDraggingInfo) -> Bool {
        return !PDFURLs(from: sender).isEmpty
    }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        let urls = PDFURLs(from: sender)
        setHighlighted(false)
        guard !urls.isEmpty else { return false }
        onFiles?(urls)
        return true
    }

    /// macOS publishes no API for its window corner radius. macOS Tahoe
    /// rounds title-bar windows by 16pt (compact-toolbar windows 20pt,
    /// full-toolbar windows 26pt); this app has a plain title bar.
    private static let windowCornerRadius: CGFloat = 16

    /// AppKit provides no automatic drop-target styling for custom views, so
    /// an accent-color outline around the window content indicates an active
    /// drop. All four corners round equally (a design choice; the window
    /// edge is only curved at the bottom of the content region).
    private func setHighlighted(_ highlighted: Bool) {
        layer?.borderColor = highlighted ? NSColor.controlAccentColor.cgColor : nil
        layer?.borderWidth = highlighted ? spec.layout.dropOutlineWidth : 0
        layer?.cornerRadius = highlighted ? Self.windowCornerRadius : 0
    }

    func setBusy(_ busy: Bool) {
        precondition(Thread.isMainThread)
        chooseButton.isEnabled = !busy
    }

    func appendLog(_ line: String) {
        precondition(Thread.isMainThread)
        guard let textStorage = logView.textStorage else { return }
        let text = textStorage.length == 0 ? line : "\n" + line
        let attributes: [NSAttributedString.Key: Any] = [
            .font: Self.logFont,
            .foregroundColor: NSColor.labelColor
        ]
        // Appending to the text storage is O(appended text); reassigning
        // logView.string re-lays-out the entire log on every line.
        textStorage.append(NSAttributedString(string: text,
                                              attributes: attributes))
        logView.scrollToEndOfDocument(nil)
    }
}

private final class AppDelegate: NSObject, NSApplicationDelegate {
    private let dropView = DropView(frame: .zero)
    private let workerQueue = DispatchQueue(label: "worker", qos: .userInitiated)
    private let flattener = PDFFlattener()
    private var window: NSWindow?
    private var launched = false
    private var pendingURLs: [URL] = []
    private var queuedBatchCount = 0
    private var terminationRequested = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        installMainMenu()
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0,
                                width: spec.layout.windowWidth, height: spec.layout.windowHeight),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = spec.name
        window.minSize = NSSize(width: spec.layout.minWindowWidth, height: spec.layout.minWindowHeight)
        window.contentView = dropView
        window.center()
        window.makeKeyAndOrderFront(nil)
        self.window = window

        dropView.onFiles = { [weak self] urls in
            self?.enqueue(urls)
        }

        launched = true
        NSApp.activate(ignoringOtherApps: true)

        if !pendingURLs.isEmpty {
            let urls = pendingURLs
            pendingURLs.removeAll()
            enqueue(urls)
        }
    }

    private func installMainMenu() {
        let appName = spec.name
        let mainMenu = NSMenu()

        let applicationMenuItem = NSMenuItem(title: appName,
                                             action: nil,
                                             keyEquivalent: "")
        let applicationMenu = NSMenu(title: appName)

        let aboutItem = applicationMenu.addItem(
            withTitle: "About \(appName)",
            action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)),
            keyEquivalent: ""
        )
        aboutItem.target = NSApp
        applicationMenu.addItem(NSMenuItem.separator())

        let servicesItem = NSMenuItem(title: "Services",
                                      action: nil,
                                      keyEquivalent: "")
        let servicesMenu = NSMenu(title: "Services")
        servicesItem.submenu = servicesMenu
        applicationMenu.addItem(servicesItem)
        NSApp.servicesMenu = servicesMenu

        applicationMenu.addItem(NSMenuItem.separator())
        applicationMenu.addItem(
            withTitle: "Hide \(appName)",
            action: #selector(NSApplication.hide(_:)),
            keyEquivalent: "h"
        )
        let hideOthersItem = applicationMenu.addItem(
            withTitle: "Hide Others",
            action: #selector(NSApplication.hideOtherApplications(_:)),
            keyEquivalent: "h"
        )
        hideOthersItem.keyEquivalentModifierMask = [.command, .option]
        applicationMenu.addItem(
            withTitle: "Show All",
            action: #selector(NSApplication.unhideAllApplications(_:)),
            keyEquivalent: ""
        )

        applicationMenu.addItem(NSMenuItem.separator())
        applicationMenu.addItem(
            withTitle: "Quit \(appName)",
            action: #selector(NSApplication.terminate(_:)),
            keyEquivalent: "q"
        )
        applicationMenuItem.submenu = applicationMenu
        mainMenu.addItem(applicationMenuItem)

        let fileMenuItem = NSMenuItem(title: "File",
                                      action: nil,
                                      keyEquivalent: "")
        let fileMenu = NSMenu(title: "File")
        let openItem = fileMenu.addItem(
            withTitle: "Open…",
            action: #selector(DropView.chooseFiles),
            keyEquivalent: "o"
        )
        openItem.target = dropView
        fileMenu.addItem(NSMenuItem.separator())
        fileMenu.addItem(
            withTitle: "Close Window",
            action: #selector(NSWindow.performClose(_:)),
            keyEquivalent: "w"
        )
        fileMenuItem.submenu = fileMenu
        mainMenu.addItem(fileMenuItem)

        NSApp.mainMenu = mainMenu
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        if launched {
            enqueue(urls)
        } else {
            pendingURLs.append(contentsOf: urls)
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        return true
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard queuedBatchCount > 0 else {
            return .terminateNow
        }
        // Don't kill the worker mid-batch: replaceItemAt is atomic so no
        // original can be corrupted, but remaining files would silently go
        // unprocessed. Finish the batch, then complete the termination.
        terminationRequested = true
        dropView.appendLog(spec.strings.finishingBeforeQuit)
        return .terminateLater
    }

    private func enqueue(_ incomingURLs: [URL]) {
        let urls = incomingURLs.filter { $0.isPDFFile }
        guard !urls.isEmpty else {
            dropView.appendLog(spec.strings.noPdfsSelected)
            return
        }

        queuedBatchCount += 1
        dropView.setBusy(true)

        workerQueue.async { [weak self] in
            guard let self = self else { return }

            for url in urls {
                let name = url.lastPathComponent
                do {
                    let result = try self.flattener.flattenInPlace(url)
                    DispatchQueue.main.async {
                        if result.changed {
                            var line = "✓ \(name) — flattened \(result.flattenedAnnotationCount) annotation\(result.flattenedAnnotationCount == 1 ? "" : "s") on \(result.pageCount) page\(result.pageCount == 1 ? "" : "s")."
                            if result.removedLinkCount > 0 {
                                line += " Note: \(result.removedLinkCount) hyperlink\(result.removedLinkCount == 1 ? "" : "s") removed."
                            }
                            self.dropView.appendLog(line)
                        } else {
                            self.dropView.appendLog("– \(name) — \(spec.strings.unchangedMessage)")
                        }
                    }
                } catch {
                    DispatchQueue.main.async {
                        self.dropView.appendLog("✗ \(name) — \(error.localizedDescription)")
                    }
                }
            }

            DispatchQueue.main.async {
                self.queuedBatchCount -= 1
                if self.queuedBatchCount == 0 {
                    self.dropView.setBusy(false)
                    NSSound(named: NSSound.Name("Glass"))?.play()
                    if self.terminationRequested {
                        self.terminationRequested = false
                        NSApp.reply(toApplicationShouldTerminate: true)
                    }
                }
            }
        }
    }
}

let application = NSApplication.shared
private let delegate = AppDelegate()
application.delegate = delegate
application.setActivationPolicy(.regular)
application.run()