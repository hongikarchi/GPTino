# -*- coding: utf-8 -*-
# GPTino dev-loop scene generator (RhinoPython / IronPython).
# NOTE: keep this file ASCII-only regardless -- IronPython 2 rejects non-ASCII source
# without the coding line, and the failure mode is a silent parse abort (no marker,
# no .scene-err, Rhino just exits).
# Builds the benchmark Rhino scene selected by $GPTINO_SCENE_KIND and saves it to
# $GPTINO_SCENE_3DM.  Kinds:
#   paneling   (default) warped surface + boundary + reveal curves + attractor points
#   structural column axis lines + perimeter beam lines + isolated test beam (Karamba)
#   hygiene    geometry with DELIBERATE audit defects (endpoint gaps, near-duplicates)
#   structural-solids  unit-prototype block instances + loose PCA brace + one deliberate
#                      free end + a mesh distractor (structural_extract live gate)
# Run via:  Rhino  /runscript="_-RunPythonScript ""scripts\dev-scene.py"" _-Exit"
# The output path is passed through the GPTINO_SCENE_3DM environment variable
# (RunPythonScript takes no CLI args). A '.scene-ok' marker is written on success;
# on failure the traceback is persisted to '<out>.scene-err' because RunPythonScript
# swallows exceptions and the driver would otherwise only see "marker missing".
import os
import traceback
import rhinoscriptsyntax as rs

out = os.environ.get("GPTINO_SCENE_3DM")
if not out:
    raise Exception("GPTINO_SCENE_3DM is not set")
kind = os.environ.get("GPTINO_SCENE_KIND", "paneling")


def _on_layer(obj, layer):
    if obj is None:
        raise Exception("geometry creation returned None (layer %s)" % layer)
    if not rs.IsLayer(layer):
        rs.AddLayer(layer)
    rs.ObjectLayer(obj, layer)


def build_structural():
    # Karamba benchmark fixture (doc units mm -- the mm->m conversion is part of what
    # the benchmark verifies). Element budget is designed for the trial cap (20 beam
    # elements): 4 columns x 1 + 4 beams x 3 subdivisions = 16 when the agent meshes it.
    #
    # Frame: one 4 m x 3 m bay, columns 3 m tall, on layers 'Columns' / 'Beams'.
    bay_x, bay_y, h = 4000, 3000, 3000
    corners = [(0, 0), (bay_x, 0), (bay_x, bay_y), (0, bay_y)]
    for corner in corners:
        x, y = corner
        _on_layer(rs.AddLine((x, y, 0), (x, y, h)), "Columns")
    for i in range(4):
        x0, y0 = corners[i]
        x1, y1 = corners[(i + 1) % 4]
        _on_layer(rs.AddLine((x0, y0, h), (x1, y1, h)), "Beams")

    # Isolated simply-supported test beam for the V2 theory check: 8 m span, placed
    # clear of the frame. Theory: rect 100x200 mm section, S235 steel, P=10 kN at
    # midspan -> delta = PL^3/48EI ~= 7.62 mm (shear deformation < 0.2% at L/h = 40).
    _on_layer(rs.AddLine((8000, -2000, 0), (16000, -2000, 0)), "TestBeam")


def build_hygiene():
    # Document-hygiene fixture: geometry carrying KNOWN, deliberate defects so the
    # audit -> approval card -> grant -> fix path runs on real findings. A fixture with
    # nothing wrong lets a live gate report PASS without ever executing the path it
    # claims to prove (the empty-InstanceDefinitions block census did exactly that).
    #
    # Tolerance is pinned here rather than inherited from whatever template Rhino
    # opened, because every gap below is expressed as a multiple of it.
    rs.UnitSystem(2, False, True)          # 2 = millimeters
    rs.UnitAbsoluteTolerance(0.001, True)

    # --- nearMissEndpoints ------------------------------------------------------
    # Detected when an endpoint-to-endpoint gap lands in (tolerance, tolerance*band],
    # i.e. (0.001, 0.01] at the default band factor of 10. Two L-corners that look
    # closed at any sane zoom and are not: exactly the defect a person cannot see.
    # Both are open curves; same-object pairs are out of scope for this kind.
    _on_layer(rs.AddLine((0, 0, 0), (5000, 0, 0)), "Walls")
    _on_layer(rs.AddLine((5000.005, 0, 0), (5000.005, 4000, 0)), "Walls")
    _on_layer(rs.AddLine((0, 8000, 0), (5000, 8000, 0)), "Walls")
    _on_layer(rs.AddLine((5000.003, 8000, 0), (5000.003, 12000, 0)), "Walls")

    # --- nearDuplicates ---------------------------------------------------------
    # Detected when max curve-to-curve deviation is <= tolerance. Offset by half a
    # tolerance: SelDup (exact match only) misses this, which is the whole point of
    # the analyzer. The endpoint gap here is 0.0005 -- BELOW tolerance -- so this pair
    # deliberately does not also surface as a near-miss.
    _on_layer(rs.AddLine((0, -3000, 0), (6000, -3000, 0)), "Slab")
    _on_layer(rs.AddLine((0, -2999.9995, 0), (6000, -2999.9995, 0)), "Slab")

    # --- purgeCandidates --------------------------------------------------------
    # An unused block definition and a genuinely empty leaf layer. 'BlockLib' holds
    # only block geometry, so it is the fixture for the safety claim that such a layer
    # must NEVER be offered for deletion as an empty leaf.
    rs.AddLayer("Scratch")
    rs.AddLayer("BlockLib")
    _marker = rs.AddCircle(rs.WorldXYPlane(), 250)
    rs.ObjectLayer(_marker, "BlockLib")
    rs.AddBlock([_marker], (0, 0, 0), "GPTinoUnusedFixture", True)


def build_structural_solids():
    # structural_extract fixture mirroring the real structural-company model's conventions:
    # each section-mark layer holds ONE unit prototype solid at the origin (exactly 1000mm
    # tall, outer dims = KS nominal x 1.02) plus InstanceReferences whose transform places,
    # rotates and stretches it. Adds a loose slender solid (PCA path), a member with one
    # DELIBERATELY unconnected end (the ask-back fixture), and a mesh (must be skipped and
    # counted, never guessed at). A defect-free fixture would let the gate pass without
    # exercising the paths it claims to prove.
    rs.UnitSystem(2, False, True)  # millimeters
    rs.UnitAbsoluteTolerance(0.001, True)

    # --- SC1 column prototype: 306x306 outer (H-300x300 nominal x 1.02), 1000 tall -------
    rs.AddLayer("Steel")
    rs.AddLayer("Steel::SC1")
    proto_col = rs.AddBox([
        (-153, -153, 0), (153, -153, 0), (153, 153, 0), (-153, 153, 0),
        (-153, -153, 1000), (153, -153, 1000), (153, 153, 1000), (-153, 153, 1000)])
    rs.ObjectLayer(proto_col, "Steel::SC1")
    rs.AddBlock([proto_col], (0, 0, 0), "SC1_proto", False)

    # --- SG1 beam prototype: 204x408 outer (H-400x200 nominal x 1.02) --------------------
    rs.AddLayer("Steel::SG1")
    proto_beam = rs.AddBox([
        (-102, -204, 0), (102, -204, 0), (102, 204, 0), (-102, 204, 0),
        (-102, -204, 1000), (102, -204, 1000), (102, 204, 1000), (-102, 204, 1000)])
    rs.ObjectLayer(proto_beam, "Steel::SG1")
    rs.AddBlock([proto_beam], (0, 0, 0), "SG1_proto", False)

    def place(block, layer, point, scale_z, angle, normal):
        # Explicit T*R*S: rs.InsertBlock composes its scale AFTER the rotation in world axes
        # (T*S*R), so a Z-scale never stretches a rotated prototype axis -- the first live gate
        # caught every rotated beam landing as a 1000mm stub. Scale the prototype along its own
        # +Z FIRST, then rotate that axis onto the run direction, then translate.
        xf_t = rs.XformTranslation(point)
        xf_r = rs.XformRotation2(angle, normal, (0, 0, 0))
        xf_s = rs.XformScale((1, 1, scale_z), (0, 0, 0))
        xf = rs.XformMultiply(rs.XformMultiply(xf_t, xf_r), xf_s)
        inst = rs.InsertBlock2(block, xf)
        rs.ObjectLayer(inst, layer)
        return inst

    # The bay sits AWAY from the origin, like the real model: the extractor treats near-origin
    # solids as parked prototypes, so a loose brace starting at (0,0) would be silently skipped
    # instead of PCA'd -- the fixture must not park real members inside the prototype zone.
    ox, oy = 20000.0, 12000.0

    # Four 3000mm columns on a 6000x4000 bay (prototype axis is +Z, so scale only).
    for corner in [(ox, oy, 0), (ox + 6000, oy, 0), (ox + 6000, oy + 4000, 0), (ox, oy + 4000, 0)]:
        place("SC1_proto", "Steel::SC1", corner, 3.0, 0, (0, 0, 1))

    # Perimeter beams at z=3000: the prototype +Z axis is rotated onto the run direction.
    place("SG1_proto", "Steel::SG1", (ox, oy, 3000), 6.0, 90, (0, 1, 0))               # +X run
    place("SG1_proto", "Steel::SG1", (ox + 6000, oy, 3000), 4.0, -90, (1, 0, 0))       # +Y run
    place("SG1_proto", "Steel::SG1", (ox + 6000, oy + 4000, 3000), 6.0, -90, (0, 1, 0))  # -X run
    place("SG1_proto", "Steel::SG1", (ox, oy + 4000, 3000), 4.0, 90, (1, 0, 0))        # -Y run

    # The ask-back fixture: a beam whose A end lands on a column top and whose B end
    # (ox-3000, oy+4000, 3000) reaches NOTHING -- intended cantilever or mistake, only the
    # human knows. Extraction reports it among the free ends (the two brace-less column
    # bases also read as free ends pre-solve; the solver's base band absorbs those).
    place("SG1_proto", "Steel::SG1", (ox, oy + 4000, 3000), 3.0, -90, (0, 1, 0))

    # Loose slender solid (no block): plan diagonal brace across the bay, 150x150 section.
    # No prototype pattern applies -- the PCA path must recover the diagonal axis.
    rs.AddLayer("Steel::BR1")
    import math
    ax, ay = ox, oy
    bx, by = ox + 6000.0, oy + 4000.0
    ux, uy = bx - ax, by - ay
    ul = math.sqrt(ux * ux + uy * uy)
    ux, uy = ux / ul, uy / ul
    px, py = -uy, ux  # in-plane perpendicular
    h = 75.0
    corners = []
    for ex, ey in ((ax, ay), (bx, by)):
        corners.extend([
            (ex - px * h, ey - py * h, -h), (ex + px * h, ey + py * h, -h),
            (ex + px * h, ey + py * h, h), (ex - px * h, ey - py * h, h)])
    brace = rs.AddBox(corners)
    rs.ObjectLayer(brace, "Steel::BR1")

    # Mesh distractor: extraction must count it as skipped, never fabricate an axis.
    rs.AddLayer("Steel::MISC")
    mesh = rs.AddMesh(
        [(10000, 10000, 0), (11000, 10000, 0), (11000, 11000, 0), (10000, 11000, 0)],
        [(0, 1, 2, 3)])
    rs.ObjectLayer(mesh, "Steel::MISC")


def build_paneling():
    # A gently warped NURBS surface to panelize (10 m x 8 m, mm units), plus its
    # boundary and a couple of freeform reveal curves and attractor points. This gives
    # the agent selectable Rhino geometry (curves / surface / points) so the
    # referenceRhinoObjects path (P0-1/P0-2/P0-3a) is exercised end to end.
    corners = [(0, 0, 0), (10000, 0, 1500), (10000, 8000, 0), (0, 8000, 2500)]
    rs.AddSrfPt(corners)

    # Closed planar boundary rectangle (area ~ 80 m^2 -> exercises area/closed predicates).
    rs.AddRectangle(rs.WorldXYPlane(), 10000, 8000)

    # Two freeform facade reveal curves.
    rs.AddCurve([(0, 2000, 0), (4000, 3000, 800), (10000, 2500, 300)])
    rs.AddCurve([(0, 6000, 0), (5000, 5000, 1200), (10000, 6500, 200)])

    # Attractor points.
    rs.AddPoint(5000, 4000, 0)
    rs.AddPoint(2000, 1000, 0)

    # Purge fixture: a block definition with no instances placed. AddBlock consumes its input
    # objects, so the definition exists and is unused -- the one thing purgeCandidates can report
    # besides empty layers. It also gives the document a non-empty InstanceDefinitions table, which
    # is what makes the layer census take its second (block-member) enumeration pass.
    #
    # The member is parked on its own layer 'BlockLib' on purpose: that layer has no top-level
    # objects, so it is the fixture for the safety-critical claim that a layer holding only block
    # geometry must never be reported as an empty leaf and offered for deletion.
    rs.AddLayer("BlockLib")
    _marker = rs.AddCircle(rs.WorldXYPlane(), 250)
    rs.ObjectLayer(_marker, "BlockLib")
    rs.AddBlock([_marker], (0, 0, 0), "GPTinoUnusedFixture", True)


try:
    # Start from a clean document.
    rs.Command("_-SelAll _Delete", False)

    if kind == "structural":
        build_structural()
    elif kind == "hygiene":
        build_hygiene()
    elif kind == "structural-solids":
        build_structural_solids()
    else:
        build_paneling()

    # Scripted SaveAs (dash-prefixed = no dialog). Path has no spaces in the dev-loop tree.
    rs.Command('_-SaveAs "%s" _Enter' % out, False)

    with open(out + ".scene-ok", "w") as handle:
        handle.write("scene generated (%s)\n" % kind)
except Exception:
    try:
        with open(out + ".scene-err", "w") as handle:
            handle.write(traceback.format_exc())
    except Exception:
        pass
    raise
