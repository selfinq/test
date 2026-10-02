import copy
import json
import os
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import synth as S  # noqa: E402
import compare as C  # noqa: E402
import snapshotlib as sl  # noqa: E402

COMPARE_PY = os.path.join(HERE, "..", "..", "tools", "difftest", "compare.py")


def cfg(**over):
    return C.merge(C.DEFAULT_CONFIG, over)


def items(rep, cid):
    return {i["path"]: i for c in rep["checkpoints"] if c["id"] == cid for i in c["items"]}


class Identical(unittest.TestCase):
    def test_identical_snapshots_match(self):
        a, b = S.build(), S.build()
        rep = C.compare(a, b, cfg(), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["verdict"], "MATCH")
        self.assertIsNone(rep["first_divergence"])
        self.assertEqual(rep["summary"]["diverge"], 0)

    def test_hash_ignores_producer_and_meta_but_not_content(self):
        a = S.build()
        b = S.clone(a)
        b["producer"] = {"name": "other"}
        b["meta"] = {"host": "x"}
        self.assertEqual(sl.snapshot_hash(a), sl.snapshot_hash(b))
        b["checkpoints"][0]["cpu"]["gpr"][4] = "0x00000099"
        self.assertNotEqual(sl.snapshot_hash(a), sl.snapshot_hash(b))


class Registers(unittest.TestCase):
    def test_register_divergence_detected_at_exact_checkpoint(self):
        a, b = S.build(), S.build(cpu_over={1: {"gpr_a0": 0xdead}})
        rep = C.compare(a, b, cfg(), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["verdict"], "DIVERGE")
        fd = rep["first_divergence"]
        self.assertEqual(fd["kind"], "checkpoint")
        self.assertEqual(fd["checkpoint"], "pc_80001000_1")
        self.assertEqual(fd["item"]["path"], "gpr.a0")
        self.assertEqual(fd["item"]["cand"], "0x0000dead")
        self.assertEqual(fd["last_good_checkpoint"], "frame_0001")

    def test_register_diff_at_frame_checkpoint_is_ignored_by_profile(self):
        a, b = S.build(), S.build(cpu_over={0: {"gpr_a0": 0xdead}})
        rep = C.compare(a, b, cfg(), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["verdict"], "MATCH")
        self.assertEqual(items(rep, "frame_0001")["cpu"]["status"], "ignored")

    def test_ignore_field_and_ignore_prefix(self):
        a, b = S.build(), S.build(cpu_over={1: {"gpr_k0": 5, "gpr_k1": 6}})
        self.assertEqual(C.compare(a, b, cfg(), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)["verdict"], "DIVERGE")
        rep = C.compare(a, b, cfg(cpu={"ignore_fields": ["gpr.k0", "gpr.k1"]}), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["verdict"], "MATCH")
        self.assertEqual(items(rep, "pc_80001000_1")["gpr.k0"]["status"], "ignored")
        rep = C.compare(a, b, cfg(cpu={"ignore_fields": ["gpr"]}), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["verdict"], "MATCH")

    def test_cop0_cause_mask(self):
        a = S.build(cpu_over={1: {"cop0_cause": 0x00000400}})
        b_masked_bit = S.build(cpu_over={1: {"cop0_cause": 0x00000000}})
        conf = cfg(cpu={"masks": {"cop0.cause": "0xfffffbff"}})   # ignore the IP2 (hardware IRQ) bit
        self.assertEqual(C.compare(a, b_masked_bit, conf, S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)["verdict"], "MATCH")
        self.assertEqual(C.compare(a, b_masked_bit, cfg(), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)["verdict"], "DIVERGE")
        b_other = S.build(cpu_over={1: {"cop0_cause": 0x00000400 | 0x80000000}})   # BD bit differs: not masked
        rep = C.compare(a, b_other, conf, S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["verdict"], "DIVERGE")
        self.assertEqual(rep["first_divergence"]["item"]["differing_bits"], "0x80000000")

    def test_candidate_without_cop2_is_unverified_not_match(self):
        a, b = S.build(), S.build()
        for cp in b["checkpoints"]:
            del cp["cpu"]["cop2_d"]
        rep = C.compare(a, b, cfg(), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["verdict"], "UNVERIFIED")
        self.assertGreater(rep["summary"]["unverified"], 0)
        rep = C.compare(a, b, cfg(cpu={"ignore_fields": ["cop2_d"]}), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["verdict"], "MATCH")


class Ram(unittest.TestCase):
    def test_ram_divergence_localised_to_page(self):
        a, b = S.build(), S.build(ram_pokes={1: [0x80005234]})
        rep = C.compare(a, b, cfg(), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        fd = rep["first_divergence"]
        self.assertEqual(fd["item"]["path"], "ram.data")
        self.assertEqual(fd["item"]["first_addr"], "0x80005200")      # page of 0x80005234 with 256 byte pages
        self.assertEqual(fd["item"]["page_range"], ["0x80005200", "0x800052ff"])
        self.assertEqual(fd["item"]["differing_pages"], 1)

    def test_ram_ignore_range_masks_only_covered_pages(self):
        a = S.build()
        b = S.build(ram_pokes={1: [0x80005234]})
        r = {"base": "0x80005200", "size": 256, "reason": "unit test volatile counter"}
        rep = C.compare(a, b, cfg(ram={"ignore_ranges": [r]}), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["verdict"], "MATCH")
        self.assertEqual(items(rep, "pc_80001000_1")["ram.data"]["status"], "ignored")
        self.assertIn("unit test volatile counter", items(rep, "pc_80001000_1")["ram.data"]["note"])
        # a poke one page further is NOT covered
        b2 = S.build(ram_pokes={1: [0x80005334]})
        rep = C.compare(a, b2, cfg(ram={"ignore_ranges": [r]}), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["verdict"], "DIVERGE")
        # partially covered page (range smaller than the page) must not hide the difference
        r_small = {"base": "0x80005200", "size": 16, "reason": "too small"}
        rep = C.compare(a, b, cfg(ram={"ignore_ranges": [r_small]}), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["verdict"], "DIVERGE")

    def test_ignore_range_requires_reason(self):
        a, b = S.build(), S.build(ram_pokes={1: [0x80005234]})
        with self.assertRaises(ValueError):
            C.compare(a, b, cfg(ram={"ignore_ranges": [{"base": "0x80005200", "size": 256}]}), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)

    def test_region_mode_ignore(self):
        a, b = S.build(), S.build(ram_pokes={1: [0x80005234]})
        rep = C.compare(a, b, cfg(ram={"regions": {"data": "ignore"}}), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["verdict"], "MATCH")

    def test_dead_stack_garbage_ignored_but_live_stack_compared(self):
        # sp = 0x8000fff0 -> live stack = [0x8000fff0, 0x80010000); garbage below is in region 'stack'
        a = S.build()
        garbage = S.build(ram_pokes={1: [0x8000e100, 0x8000f000]})
        rep = C.compare(a, garbage, cfg(), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["verdict"], "MATCH", rep["first_divergence"])
        self.assertEqual(items(rep, "pc_80001000_1")["ram.stack"]["status"], "ignored")
        self.assertEqual(items(rep, "pc_80001000_1")["ram.stack_live"]["status"], "match")
        live = S.build(ram_pokes={1: [0x8000fff4]})
        rep = C.compare(a, live, cfg(), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["first_divergence"]["item"]["path"], "ram.stack_live")
        # if the dead-stack exclusion is removed the garbage is a divergence again
        rep = C.compare(a, garbage, cfg(ram={"regions": {"stack": "exact"}}), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["first_divergence"]["item"]["path"], "ram.stack")

    def test_frame_profile_compares_static_pages_only(self):
        a = S.build()
        # 0x80008000 / 0x80008100 pages are volatile in the reference (they change between its checkpoints)
        vol = S.build(ram_pokes={0: [0x80008010], 2: [0x80008110]})
        rep = C.compare(a, vol, cfg(), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["verdict"], "MATCH")
        self.assertEqual(items(rep, "frame_0001")["ram.bss"]["status"], "ignored")
        self.assertIn("volatile pages skipped", items(rep, "frame_0001")["ram.bss"]["note"])
        # a static page (never changes in the reference) is compared at frame checkpoints
        static = S.build(ram_pokes={0: [0x80000010]})
        rep = C.compare(a, static, cfg(), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["verdict"], "DIVERGE")
        fd = rep["first_divergence"]
        self.assertEqual((fd["checkpoint"], fd["item"]["path"], fd["item"]["first_addr"]), ("frame_0001", "ram.code", "0x80000000"))

    def test_candidate_without_pages_is_unverified_for_static_pages(self):
        a, b = S.build(), S.build()
        for cp in b["checkpoints"]:
            for e in cp["ram"]["regions"].values():
                del e["pages"]
        rep = C.compare(a, b, cfg(), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["verdict"], "UNVERIFIED")


class Events(unittest.TestCase):
    def test_chain_divergence_at_checkpoint(self):
        evs = list(S.DEFAULT_EVENTS)
        evs[1] = S.W(0x1f8010a0, 0x80999999)
        a, b = S.build(), S.build(events=evs)
        rep = C.compare(a, b, cfg(), None, None)         # no sidecars: only chains
        fd = rep["first_divergence"]
        self.assertEqual((fd["kind"], fd["checkpoint"], fd["item"]["path"]), ("checkpoint", "pc_80001000_1", "events.w"))
        self.assertEqual(rep["events"]["status"], "unverified")

    def test_first_event_divergence_with_context(self):
        evs = list(S.DEFAULT_EVENTS)
        evs[2] = S.D(2, 0x01000401, 0x80123456, 0, 12, 0x9999)
        a, b = S.build(), S.build(events=evs)
        rep = C.compare(a, b, cfg(), S.DEFAULT_EVENTS, evs, )
        fd = rep["first_divergence"]
        self.assertEqual(fd["kind"], "event")
        self.assertEqual(fd["event"]["ref_index"], 2)
        self.assertIn("0000000000001234", fd["event"]["ref_text"])
        self.assertIn("0000000000009999", fd["event"]["cand_text"])
        self.assertEqual([w["index"] for w in fd["event"]["ref_context"]], [0, 1, 2, 3])
        text = C.format_report(rep)
        self.assertIn("FIRST DIVERGENCE (event)", text)
        self.assertIn(">> #2", text)

    def test_event_divergence_before_checkpoint_divergence_wins(self):
        evs = list(S.DEFAULT_EVENTS)
        evs[0] = S.W(0x1f801810, 0xe1001001)
        a = S.build()
        b = S.build(events=evs, cpu_over={1: {"gpr_a0": 7}})
        rep = C.compare(a, b, cfg(), S.DEFAULT_EVENTS, evs)
        self.assertEqual(rep["first_divergence"]["kind"], "event")
        self.assertEqual(rep["first_divergence"]["event"]["ref_index"], 0)
        # the register divergence is still listed
        self.assertEqual(rep["checkpoints"][1]["status"], "diverge")

    def test_checkpoint_divergence_before_event_divergence_wins(self):
        evs = list(S.DEFAULT_EVENTS)
        evs[3] = S.W(0x1f801814, 0x03000001)           # after the last checkpoint position (4 events at ckpt 3)
        a = S.build()
        b = S.build(events=evs, cpu_over={1: {"gpr_a0": 7}})
        rep = C.compare(a, b, cfg(), S.DEFAULT_EVENTS, evs)
        self.assertEqual(rep["first_divergence"]["kind"], "checkpoint")
        self.assertEqual(rep["first_divergence"]["checkpoint"], "pc_80001000_1")

    def test_event_ignore_range_and_kind(self):
        evs = list(S.DEFAULT_EVENTS)
        evs[0] = S.W(0x1f080010, 1)
        evs2 = list(evs)
        evs2[0] = S.W(0x1f080010, 2)
        a, b = S.build(events=evs), S.build(events=evs2)
        rep = C.compare(a, b, cfg(), evs, evs2)
        self.assertEqual(rep["first_divergence"]["kind"], "event")      # event #0 differs, before the first checkpoint
        self.assertEqual(rep["first_divergence"]["event"]["ref_index"], 0)
        conf = cfg(events={"ignore_ranges": [{"base": "0x1f080000", "size": 0x4000, "reason": "sound CPU shared RAM"}]})
        rep = C.compare(a, b, conf, evs, evs2)
        self.assertEqual(rep["verdict"], "MATCH")
        self.assertEqual(items(rep, "pc_80001000_1")["events"]["status"], "ignored")
        self.assertEqual(rep["events"]["status"], "match")
        # ignoring all reads must not hide a write difference
        evs3 = list(S.DEFAULT_EVENTS)
        evs3[1] = S.W(0x1f8010a0, 0x80000001)
        rep = C.compare(a, S.build(events=evs3), cfg(events={"ignore_kinds": ["R"]}), evs, evs3)
        self.assertEqual(rep["first_divergence"]["kind"], "event")

    def test_candidate_event_stream_shorter(self):
        short = S.DEFAULT_EVENTS[:3]
        a = S.build()
        rep = C.compare(a, S.build(events=short, splits=(1, 2, 3)), cfg(), S.DEFAULT_EVENTS, short)
        self.assertIn("shorter", rep["events"]["note"])
        self.assertEqual(rep["verdict"], "MATCH")       # common prefix identical; frame checkpoints ignore event chains
        rep = C.compare(a, S.build(events=short, splits=(1, 2, 3)), cfg(events={"length": "strict"}), S.DEFAULT_EVENTS, short)
        self.assertEqual(rep["events"]["status"], "diverge")
        self.assertEqual(rep["events"]["reason"], "length")


class Checkpoints(unittest.TestCase):
    def test_missing_checkpoint_unverified_unless_required(self):
        a, b = S.build(), S.build(drop=("pc_80001000_1",))
        rep = C.compare(a, b, cfg(), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["verdict"], "UNVERIFIED")
        rep = C.compare(a, b, cfg(require_ids=["pc_80001000_1"]), S.DEFAULT_EVENTS, S.DEFAULT_EVENTS)
        self.assertEqual(rep["verdict"], "DIVERGE")
        self.assertEqual(rep["first_divergence"]["checkpoint"], "pc_80001000_1")

    def test_ids_filter(self):
        a, b = S.build(), S.build(cpu_over={1: {"gpr_a0": 1}})
        rep = C.compare(a, b, cfg(ids=["frame_0001", "frame_0002"]), None, None)
        self.assertEqual(len(rep["checkpoints"]), 2)
        self.assertNotEqual(rep["first_divergence"] and rep["first_divergence"].get("checkpoint"), "pc_80001000_1")


class CLI(unittest.TestCase):
    def run_cli(self, ref, cand, *extra, ref_events=None, cand_events=None):
        with tempfile.TemporaryDirectory() as d:
            rp, cp = os.path.join(d, "ref.json"), os.path.join(d, "cand.json")
            with open(rp, "w") as f:
                json.dump(ref, f)
            with open(cp, "w") as f:
                json.dump(cand, f)
            args = [sys.executable, COMPARE_PY, rp, cp] + list(extra)
            if ref_events is not None:
                ep = os.path.join(d, "ref.events.jsonl")
                with open(ep, "w") as f:
                    f.write("".join(json.dumps(e) + "\n" for e in ref_events))
                args += ["--events-ref", ep]
            if cand_events is not None:
                ep = os.path.join(d, "cand.events.jsonl")
                with open(ep, "w") as f:
                    f.write("".join(json.dumps(e) + "\n" for e in cand_events))
                args += ["--events-cand", ep]
            return subprocess.run(args, capture_output=True, text=True)

    def test_exit_codes(self):
        a = S.build()
        r = self.run_cli(a, S.build(), ref_events=S.DEFAULT_EVENTS, cand_events=S.DEFAULT_EVENTS)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("VERDICT: MATCH", r.stdout)
        r = self.run_cli(a, S.build(cpu_over={1: {"gpr_a0": 1}}), ref_events=S.DEFAULT_EVENTS, cand_events=S.DEFAULT_EVENTS)
        self.assertEqual(r.returncode, 1)
        self.assertIn("FIRST DIVERGENCE (checkpoint)", r.stdout)
        self.assertIn("gpr.a0", r.stdout)
        r = self.run_cli(a, S.build())                      # no sidecars: unverified, exit 0 ...
        self.assertEqual(r.returncode, 0)
        self.assertIn("UNVERIFIED", r.stdout)
        r = self.run_cli(a, S.build(), "--strict-unverified")   # ... but 3 when strict
        self.assertEqual(r.returncode, 3)

    def test_invalid_snapshot_exit_2(self):
        bad = S.build()
        bad["schema"] = "nope/9"
        r = self.run_cli(S.build(), bad)
        self.assertEqual(r.returncode, 2)
        self.assertIn("schema must be", r.stderr)

    def test_json_output_parses(self):
        r = self.run_cli(S.build(), S.build(), "--json", ref_events=S.DEFAULT_EVENTS, cand_events=S.DEFAULT_EVENTS)
        rep = json.loads(r.stdout)
        self.assertEqual(rep["verdict"], "MATCH")

    def test_default_config_file_matches_embedded_default(self):
        p = os.path.join(HERE, "..", "..", "tools", "difftest", "compare_config.default.json")
        with open(p) as f:
            self.assertEqual(json.load(f), C.DEFAULT_CONFIG)


if __name__ == "__main__":
    unittest.main()
