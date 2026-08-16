# WCAG 2.1 A/AA Accessibility Assessment

## Document Status

| Field | Value |
| --- | --- |
| Component | `WinUIEx.Maps.MapControl` |
| Standard | WCAG 2.1, Levels A and AA |
| Assessment type | Living engineering assessment |
| Last reviewed | 2026-08-16 |
| Formal conformance claim | No |

This document records the current accessibility evidence, known gaps, and
planned remediation for the reusable WinUI control. It is not an Accessibility
Conformance Report (ACR), VPAT, certification, or legal claim of conformance.
The host application remains responsible for its surrounding UI, content,
configuration, custom layers, and end-to-end user experience.

## Status Definitions

| Status | Meaning |
| --- | --- |
| **Supports** | Available evidence currently demonstrates the criterion for the control's applicable built-in behavior. Manual product-level validation may still be required. |
| **Partially Supports** | Some applicable built-in behavior is covered, but a material path, content type, input mode, or accessibility surface is missing or unverified. |
| **Does Not Support** | An applicable requirement is known not to be met. |
| **Not Applicable** | The criterion does not apply to this reusable control's built-in behavior. It may still apply to the host application or application-authored content. |
| **Not Evaluated** | The criterion may apply, but sufficient targeted evidence has not yet been collected. |

Statuses are intentionally conservative. The existence of an API, style name,
or implementation plan is not sufficient evidence for **Supports**.

## Current Summary

| Status | Level A | Level AA | Total |
| --- | ---: | ---: | ---: |
| Supports | 7 | 2 | 9 |
| Partially Supports | 8 | 5 | 13 |
| Does Not Support | 1 | 0 | 1 |
| Not Applicable | 11 | 8 | 19 |
| Not Evaluated | 3 | 5 | 8 |
| **Total** | **30** | **20** | **50** |

## Scope and Evidence Rules

The assessment includes:

- The `MapControl` root, its built-in input behavior, focus treatment, Azure
  base-map rendering, attribution, authentication notification, and built-in
  lightweight map elements.
- Public configuration that changes the control's accessibility behavior.
- Behavior directly verified through implementation review or focused testing.

The assessment excludes:

- The host application's page title, navigation, language, instructions, and
  surrounding controls.
- Semantics, colors, text alternatives, and interaction supplied by custom
  raster servers, custom icons, or application-authored layer content, except
  where the control provides and verifies an accessibility contract.
- Formal testing with Narrator, NVDA, Accessibility Insights, high-contrast
  themes, display scaling, all Windows text-scale settings, and all input
  devices unless explicitly cited below.

## Level A

| Criterion | Status | Current evidence and scope | Gap or planned action |
| --- | --- | --- | --- |
| **1.1.1 Non-text Content** | **Does Not Support** | The map surface, Azure vector features, raster tiles, polygons, polylines, and icons are rendered into a swap chain. Attribution alone has an automation name and live-region peer. | No equivalent text alternative describes the displayed map or application-authored pixel layers. Add vector-derived viewport summaries, map-element alternatives, and author-supplied descriptions for nonsemantic layers. |
| **1.2.1 Audio-only and Video-only (Prerecorded)** | **Not Applicable** | The control has no built-in prerecorded audio or video. | Host applications remain responsible for media they place around or over the map. |
| **1.2.2 Captions (Prerecorded)** | **Not Applicable** | The control has no built-in prerecorded synchronized media. | Host application responsibility. |
| **1.2.3 Audio Description or Media Alternative (Prerecorded)** | **Not Applicable** | The control has no built-in prerecorded synchronized media. | Host application responsibility. |
| **1.3.1 Info and Relationships** | **Partially Supports** | The map root is a focusable WinUI `Control`; attribution is exposed as content with a polite live setting, and attribution links retain hyperlink semantics. | Geographic features, layer relationships, camera state, and lightweight `MapElement` instances have no UIA structure. Add semantic viewport data, descriptions, and virtual automation children, then validate them with assistive technology. |
| **1.3.2 Meaningful Sequence** | **Not Evaluated** | Visual layer and element ordering are deterministic, and attribution inline order is preserved. | No screen-reader traversal exists for geographic content, so meaningful automation order cannot yet be assessed. Define `MapTabIndex`, stable virtual-peer ordering, and focused traversal tests. |
| **1.3.3 Sensory Characteristics** | **Partially Supports** | Core pan and zoom operations have keyboard alternatives and do not require instructions based solely on shape, location, sound, or orientation. | Rotate, pitch, map-element selection, and a nonvisual description of geographic state are incomplete. Add equivalent keyboard commands and screen-reader descriptions. |
| **1.4.1 Use of Color** | **Not Evaluated** | Multiple Azure styles, including high-contrast light and dark, are available. | There is no targeted audit proving that built-in information is never conveyed by color alone across styles and element types. Add automatic high-contrast behavior and perform a targeted visual audit; application-authored layers remain application responsibility. |
| **1.4.2 Audio Control** | **Not Applicable** | The control does not play audio. | None for built-in behavior. |
| **2.1.1 Keyboard** | **Partially Supports** | A focused map can pan with arrow keys and zoom with plus/minus; these behaviors have focused live testing. | Keyboard rotate, pitch, description detail, Escape behavior, and map-element traversal/invocation are missing. Add and test those keyboard paths. |
| **2.1.2 No Keyboard Trap** | **Supports** | The map is a single tab stop and does not currently create an internal keyboard-focus cycle. | Re-evaluate when virtual map-element focus and traversal are added. |
| **2.1.4 Character Key Shortcuts** | **Supports** | Plus and minus shortcuts operate only while the map itself has focus. The control does not install application-wide single-character shortcuts. | Re-evaluate future description shortcuts and keep them modifier-qualified. |
| **2.2.1 Timing Adjustable** | **Not Applicable** | Built-in tasks do not impose a user-response time limit. | Host application responsibility. |
| **2.2.2 Pause, Stop, Hide** | **Not Applicable** | The control has no built-in automatically updating, blinking, or scrolling information that starts independently and persists for more than five seconds. Camera movement is user- or application-initiated and finite. | Re-evaluate if continuously updating overlays become built-in features. |
| **2.3.1 Three Flashes or Below Threshold** | **Supports** | Built-in rendering and camera animations do not intentionally flash content. | Custom tile imagery and application-authored layers remain application responsibility; add targeted visual review to the accessibility test matrix. |
| **2.4.1 Bypass Blocks** | **Not Applicable** | Bypass navigation is a page/application concern; the control is one focusable component. | Host application responsibility. |
| **2.4.2 Page Titled** | **Not Applicable** | The control does not own a page or window title. | Host application responsibility. |
| **2.4.3 Focus Order** | **Partially Supports** | The root participates in normal XAML tab order and shows a focus state. | Lightweight map elements expose neither focus nor `MapTabIndex`. Implement and test deterministic logical traversal. |
| **2.4.4 Link Purpose (In Context)** | **Partially Supports** | Visible attribution link text is also assigned as its automation name. | Link purpose depends on provider-supplied attribution text and has not been manually assessed. Application-provided interactive map content has no accessible context. |
| **2.5.1 Pointer Gestures** | **Supports** | Multipoint pinch/stretch and touch pan have single-pointer or keyboard alternatives: wheel, double-tap, plus/minus, and arrow keys. | Re-evaluate rotation after adding its keyboard equivalent. |
| **2.5.2 Pointer Cancellation** | **Not Evaluated** | Tap, right-tap, pointer, and manipulation handlers use WinUI routed input, but no focused test assesses down-event activation, cancellation, or undo semantics for all map interactions. | Add pointer cancellation and drag-threshold tests, including future map-element invocation. |
| **2.5.3 Label in Name** | **Supports** | Attribution hyperlinks use their visible text as the UIA name. The root has no built-in visible text label. | Require accessible names for interactive map elements and test that any visible title is contained in the accessible name. |
| **2.5.4 Motion Actuation** | **Not Applicable** | The control does not use device motion or user gesture motion detected by sensors. Touch manipulation is pointer input covered by 2.5.1. | Host application responsibility for sensor-driven camera behavior. |
| **3.1.1 Language of Page** | **Not Applicable** | The control does not own the host page's default language. | Host applications must set the appropriate language. Generated map descriptions must use the control's effective language and culture. |
| **3.2.1 On Focus** | **Supports** | Receiving focus changes only the focus visual and does not initiate a context change. | Re-evaluate when virtual map-element focus is added. |
| **3.2.2 On Input** | **Supports** | Keyboard, pointer, and touch input perform the requested map operation without unrelated navigation or a change of application context. | Re-evaluate when accessible map-element invocation is added. |
| **3.3.1 Error Identification** | **Partially Supports** | Missing or invalid Azure authentication is surfaced through a visible WinUI `InfoBar`. | The authentication message's screen-reader announcement and clarity have not been tested; other acquisition failures are diagnostics-only by design. Add focused UIA and screen-reader testing. |
| **3.3.2 Labels or Instructions** | **Partially Supports** | The authentication `InfoBar` can present built-in explanatory text, and attribution links have names. | The map root has no default accessible help text describing keyboard operation, and application-authored interactive elements have no naming contract. Add localized root help text and map-element naming guidance. |
| **4.1.1 Parsing** | **Not Applicable** | This is a compiled native WinUI control, not markup delivered to a user agent. XAML template validity is enforced by the build. | Host application markup remains application responsibility. |
| **4.1.2 Name, Role, Value** | **Partially Supports** | A public sealed `MapControlAutomationPeer` reports class name `MapControl`, preserves standard attached automation properties, and exposes Scroll, Transform, and Transform2 patterns with live camera values and operations. Attribution and hyperlinks expose native automation peers. | The map peer still has no semantic geographic children, viewport description, or built-in accessible name/help behavior. Add those automation semantics and test their values and lifecycle. |

## Level AA

| Criterion | Status | Current evidence and scope | Gap or planned action |
| --- | --- | --- | --- |
| **1.2.4 Captions (Live)** | **Not Applicable** | The control has no built-in live audio. | Host application responsibility. |
| **1.2.5 Audio Description (Prerecorded)** | **Not Applicable** | The control has no built-in prerecorded video. | Host application responsibility. |
| **1.3.4 Orientation** | **Supports** | The control has no portrait/landscape restriction and lays out from its assigned size. | Validate representative narrow, wide, rotated-device, and resized-window layouts in the accessibility test matrix. |
| **1.3.5 Identify Input Purpose** | **Not Applicable** | The control contains no fields collecting user information. | Host application responsibility. |
| **1.4.3 Contrast (Minimum)** | **Not Evaluated** | High-contrast Azure styles exist, attribution uses black on an opaque white background, and the authentication message uses WinUI theme resources. | No contrast measurement covers every built-in style, label, attribution state, focus state, or composited background. Add dynamic high-contrast behavior and complete contrast measurements. |
| **1.4.4 Resize Text** | **Partially Supports** | `UISettings.TextScaleFactor` changes are monitored and applied to vector labels when `IsTextScaleFactorEnabled` is true; XAML attribution participates in platform text scaling. | The full 200% experience, collision behavior, clipping, custom icons, authentication text, and generated accessibility text have not been assessed. Validate them across supported cultures and scale settings. |
| **1.4.5 Images of Text** | **Partially Supports** | Azure vector labels are rendered from text properties and scale with the system text factor rather than arriving as fixed raster imagery. | Raster tiles and custom icon imagery can contain text without an alternative, and the control cannot detect it. Provide map-element and layer descriptions; content authors remain responsible. |
| **1.4.10 Reflow** | **Not Evaluated** | The renderer responds to control size changes, and attribution wraps within a maximum width. | No 400% zoom or equivalent narrow-viewport assessment proves two-dimensional scrolling is avoided where applicable. Add resize and display-scale coverage. |
| **1.4.11 Non-text Contrast** | **Not Evaluated** | A visible focus outline uses WinUI theme resources, and high-contrast map styles are available. | Map controls, focus indicators over varied imagery, geographic boundaries, and custom elements have not been measured. Add dynamic high-contrast behavior and perform those measurements. |
| **1.4.12 Text Spacing** | **Not Evaluated** | Attribution is ordinary wrapping XAML text; vector labels are renderer-owned glyphs. | The control exposes no character, word, line, or paragraph spacing contract for vector labels, and no override audit has been run. Evaluate applicability and clipping with targeted tests. |
| **1.4.13 Content on Hover or Focus** | **Not Applicable** | The control has no built-in tooltip, popup, or transient content triggered solely by hover or keyboard focus. | Re-evaluate if accessible feature popups become built-in. |
| **2.4.5 Multiple Ways** | **Not Applicable** | Locating pages within a set of pages is outside this control's scope. | Host application responsibility. |
| **2.4.6 Headings and Labels** | **Partially Supports** | Attribution hyperlinks expose their visible label, and the authentication surface uses a native `InfoBar`. | The map root lacks a useful default accessible name/help text, and map elements have no labels. Add localized root and element labels. |
| **2.4.7 Focus Visible** | **Supports** | Keyboard focus activates a dedicated two-pixel theme-resource focus outline; pointer focus intentionally uses a separate state. | Measure contrast over all map imagery and Windows contrast themes. |
| **3.1.2 Language of Parts** | **Not Evaluated** | Vector labels can resolve localized text, but no automation description is currently generated. | Generated descriptions must preserve language metadata or avoid mixing unmarked languages where the platform cannot represent it; assess provider attribution text separately. |
| **3.2.3 Consistent Navigation** | **Not Applicable** | Repeated page-level navigation mechanisms are outside this control's scope. | Host application responsibility. |
| **3.2.4 Consistent Identification** | **Not Applicable** | Cross-page component identification is a host-application concern. | Keep future built-in controls and virtual peers consistently named, then re-evaluate. |
| **3.3.3 Error Suggestion** | **Partially Supports** | The Azure authentication `InfoBar` can explain that a map token is required rather than exposing only a numeric failure. | Suggestions have not been reviewed with assistive technology, and no accessible remediation contract exists for custom layer failures. Validate the built-in message with screen readers and UIA. |
| **3.3.4 Error Prevention (Legal, Financial, Data)** | **Not Applicable** | The control does not submit legal commitments, financial transactions, or user-controlled data changes. | Host application responsibility. |
| **4.1.3 Status Messages** | **Partially Supports** | Attribution changes are exposed as a polite live region and explicitly raise `LiveRegionChanged`. | Camera changes, viewport descriptions, loading outcomes, and accessible selection changes are not announced. Add debounced map-state announcements and focused assistive-technology tests. |

## References

- [Web Content Accessibility Guidelines (WCAG) 2.1](https://www.w3.org/TR/WCAG21/)
- [Microsoft Accessibility Conformance Reports](https://www.microsoft.com/accessibility/conformance-reports)
- [Azure Maps accessibility](https://learn.microsoft.com/azure/azure-maps/map-accessibility)
- [UWP `MapControlAutomationPeer`](https://learn.microsoft.com/uwp/api/windows.ui.xaml.automation.peers.mapcontrolautomationpeer)
