import { useCallback, useEffect, useRef, useState } from "react";
import {
  CurrentStateAuthorityUnavailableFailure,
  CurrentStateInvalidMachineFailure,
  type CurrentMachineStateResponse,
} from "../api/current-state/current-state-client.ts";
import type { DashboardApplicationRuntime } from "./application-runtime.ts";

export type MachineCurrentStateViewState =
  | { kind: "loading" }
  | { kind: "success"; data: CurrentMachineStateResponse; refreshing: boolean }
  | { kind: "authorityUnavailable" }
  | { kind: "invalidMachine" }
  | { kind: "failed" };

export function useMachineCurrentState(
  machineId: string,
  runtime: DashboardApplicationRuntime,
): { state: MachineCurrentStateViewState; refresh: () => Promise<void> } {
  const [state, setState] = useState<MachineCurrentStateViewState>({ kind: "loading" });
  const generation = useRef(0);
  const active = useRef<AbortController | undefined>(undefined);
  const mounted = useRef(true);

  const execute = useCallback(async () => {
    active.current?.abort(Symbol("current-state-superseded"));
    const controller = new AbortController();
    active.current = controller;
    const ownGeneration = ++generation.current;

    setState(previous => previous.kind === "success"
      ? { ...previous, refreshing: true }
      : { kind: "loading" });

    try {
      const data = await runtime.currentStateClient.read(machineId, { signal: controller.signal });
      if (mounted.current && generation.current === ownGeneration) {
        setState({ kind: "success", data, refreshing: false });
      }
    } catch (error) {
      if (!mounted.current || generation.current !== ownGeneration || controller.signal.aborted) {
        return;
      }
      if (error instanceof CurrentStateAuthorityUnavailableFailure) {
        setState({ kind: "authorityUnavailable" });
      } else if (error instanceof CurrentStateInvalidMachineFailure) {
        setState({ kind: "invalidMachine" });
      } else {
        setState({ kind: "failed" });
      }
    }
  }, [machineId, runtime.currentStateClient]);

  useEffect(() => {
    mounted.current = true;
    void execute();
    return () => {
      mounted.current = false;
      active.current?.abort(Symbol("current-state-disposed"));
    };
  }, [execute]);

  return { state, refresh: execute };
}
