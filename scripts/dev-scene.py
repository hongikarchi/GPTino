# -*- coding: utf-8 -*-
# GPTino dev-loop scene generator (RhinoPython / IronPython).
# NOTE: keep this file ASCII-only regardless -- IronPython 2 rejects non-ASCII source
# without the coding line, and the failure mode is a silent parse abort (no marker,
# no .scene-err, Rhino just exits).
# Builds the benchmark Rhino scene selected by $GPTINO_SCENE_KIND and saves it to
# $GPTINO_SCENE_3DM.  Kinds:
#   paneling   (default) warped surface + boundary + reveal curves + attractor points
#   structural column axis lines + perimeter beam lines + isolated test beam (Karamba)
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
