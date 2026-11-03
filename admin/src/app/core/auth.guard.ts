import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { ApiService } from './api.service';

export const authGuard: CanActivateFn = () => inject(ApiService).loggedIn() ? true : inject(Router).createUrlTree(['/login']);
