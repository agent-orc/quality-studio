import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

/** Presentational connection state, independent of transport and repository services. */
@Component({
  selector: 'qs-api-connection-state',
  templateUrl: './api-connection-state.html',
  styleUrl: './api-connection-state.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ApiConnectionState {
  readonly state = input.required<string>();
  readonly error = input('');
  readonly retrying = input(false);
  readonly retry = output<void>();
  readonly configure = output<void>();
}
